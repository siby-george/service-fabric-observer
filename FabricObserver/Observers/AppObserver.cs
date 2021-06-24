﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Newtonsoft.Json;
using ConfigSettings = FabricObserver.Observers.MachineInfoModel.ConfigSettings;

namespace FabricObserver.Observers
{
    // This observer monitors the behavior of user SF service processes (and their children) and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json. This observer will also emit telemetry (ETW, LogAnalytics/AppInsights) if enabled in Settings.xml (ObserverManagerConfiguration) and ApplicationManifest.xml (AppObserverEnableEtw).
    public class AppObserver : ObserverBase
    {
        // Health Report data containers - For use in analysis to determine health state.
        // These lists are cleared after each healthy iteration.
        private List<FabricResourceUsageData<double>> AllAppCpuData;
        private List<FabricResourceUsageData<float>> AllAppMemDataMb;
        private List<FabricResourceUsageData<double>> AllAppMemDataPercent;
        private List<FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private List<FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private List<FabricResourceUsageData<float>> AllAppHandlesData;

        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private List<ApplicationInfo> deployedTargetList;
        private readonly ConfigSettings configSettings;
        private string fileName;
        private readonly Stopwatch stopwatch;

        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList
        {
            get; set;
        }

        public string ConfigPackagePath
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppObserver"/> class.
        /// </summary>
        public AppObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            configSettings = new ConfigSettings(FabricServiceContext);
            ConfigPackagePath = configSettings.ConfigPackagePath;

            stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            stopwatch.Start();
            bool initialized = await InitializeAsync();
            Token = token;

            if (!initialized)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                                FabricServiceContext.ServiceName.OriginalString,
                                ObserverName,
                                HealthState.Warning,
                                "This observer was unable to initialize correctly due to missing configuration info.");

                stopwatch.Stop();
                stopwatch.Reset();

                return;
            }

            await MonitorDeployedAppsAsync(token).ConfigureAwait(true);
            await ReportAsync(token).ConfigureAwait(true);

            // The time it took to run this observer.
            stopwatch.Stop();
            CleanUp();
            RunDuration = stopwatch.Elapsed;
            
            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopwatch.Reset();

            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            var healthReportTimeToLive = GetHealthReportTimeToLive();

            foreach (var repOrInst in ReplicaOrInstanceList)
            {
                token.ThrowIfCancellationRequested();

                string processName = null;
                int processId = 0;
                ApplicationInfo app = null; 

                try
                {
                    app = deployedTargetList.Find(
                        a => a.TargetApp == repOrInst.ApplicationName.OriginalString || a.TargetAppType == repOrInst.ApplicationTypeName);
                    
                    using Process p = Process.GetProcessById((int)repOrInst.HostProcessId);

                    // If the process is no longer running, then don't report on it.
                    if (p.HasExited)
                    {
                        continue;
                    }

                    processName = p.ProcessName;
                    processId = p.Id;
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    ObserverLogger.LogWarning($"Handled Exception in ReportAsync:{Environment.NewLine}{e}");
                    continue;
                }

                string appNameOrType = GetAppNameOrType(repOrInst);
                var id = $"{appNameOrType}:{processName}";

                // Locally Log (csv) CPU/Mem/FileHandles/Ports per app service process.
                if (EnableCsvLogging)
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (processName == "Fabric")
                    {
                        continue;
                    }

                    fileName = $"{processName}{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";

                    // BaseLogDataLogFolderPath is set in ObserverBase or a default one is created by CsvFileLogger.
                    // This means a new folder will be added to the base path.
                    if (CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives)
                    {
                        CsvFileLogger.DataLogFolder = processName;
                    }

                    // Log pid..
                    CsvFileLogger.LogData(fileName, id, "ProcessId", "", processId);

                    // Log resource usage data to CSV files.
                    LogAllAppResourceDataToCsv(id);
                }

                // CPU - Parent process
                if (AllAppCpuData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppCpuData.FirstOrDefault(x => x.Id == id);
                    var parentDataAvg = Math.Round(parentFrud.AverageDataValue);
                    double sumValues = Math.Round(parentDataAvg, 0);
                    sumValues += ProcessChildFrudsGetDataSum(ref AllAppCpuData, processName, repOrInst, app, token);

                    // This will only be true if the parent has child procs that are currently executing.
                    if (sumValues > parentDataAvg)
                    {
                        parentFrud.Data.Clear();
                        parentFrud.Data.Add(sumValues);
                    }

                    ProcessResourceDataReportHealth(
                            parentFrud,
                            app.CpuErrorLimitPercent,
                            app.CpuWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                }

                // Memory MB - Parent process
                if (AllAppMemDataMb.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppMemDataMb.FirstOrDefault(x => x.Id == id);
                    var parentDataAvg = Math.Round(parentFrud.AverageDataValue);
                    double sumValues = Math.Round(parentDataAvg, 0);
                    sumValues += ProcessChildFrudsGetDataSum(ref AllAppMemDataMb, processName, repOrInst, app, token);

                    if (sumValues > parentDataAvg)
                    {
                        parentFrud.Data.Clear();
                        parentFrud.Data.Add((float)sumValues);
                    }

                    // Parent's aggregated (summed) spawned process data.
                    // This will generate an SF health event if the combined total exceeds the supplied threshold.
                    ProcessResourceDataReportHealth(
                            parentFrud,
                            app.MemoryErrorLimitMb,
                            app.MemoryWarningLimitMb,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                }

                // Memory Percent - Parent process
                if (AllAppMemDataPercent.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppMemDataPercent.FirstOrDefault(x => x.Id == id);
                    var parentDataAvg = Math.Round(parentFrud.AverageDataValue);
                    double sumValues = Math.Round(parentDataAvg, 0);
                    sumValues += ProcessChildFrudsGetDataSum(ref AllAppMemDataPercent, processName, repOrInst, app, token);

                    if (sumValues > parentDataAvg)
                    {
                        parentFrud.Data.Clear();
                        parentFrud.Data.Add(sumValues);
                    }

                    // Parent's aggregated (summed) spawned process data.
                    ProcessResourceDataReportHealth(
                            parentFrud,
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);   
                }

                // TCP Ports - Active - Parent process
                if (AllAppTotalActivePortsData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == id);
                    var parentDataAvg = Math.Round(parentFrud.AverageDataValue);
                    double sumValues = Math.Round(parentDataAvg, 0);
                    sumValues += ProcessChildFrudsGetDataSum(ref AllAppTotalActivePortsData, processName, repOrInst, app, token);

                    if (sumValues > parentDataAvg)
                    {
                        parentFrud.Data.Clear();
                        parentFrud.Data.Add((int)sumValues);
                    }

                    ProcessResourceDataReportHealth(
                            parentFrud,
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                }

                // TCP Ports - Ephemeral (port numbers fall in the dynamic range) - Parent process
                if (AllAppEphemeralPortsData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == id);
                    var parentDataAvg = Math.Round(parentFrud.AverageDataValue);
                    double sumValues = Math.Round(parentDataAvg, 0);
                    sumValues += ProcessChildFrudsGetDataSum(ref AllAppEphemeralPortsData, processName, repOrInst, app, token);

                    if (sumValues > parentDataAvg)
                    {
                        parentFrud.Data.Clear();
                        parentFrud.Data.Add((int)sumValues);
                    }

                    // Parent's aggregated (summed) process data.
                    ProcessResourceDataReportHealth(
                            parentFrud,
                            app.NetworkErrorEphemeralPorts,
                            app.NetworkWarningEphemeralPorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                }

                // Allocated (in use) Handles - Parent process
                if (AllAppHandlesData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppHandlesData.FirstOrDefault(x => x.Id == id);
                    var parentDataAvg = Math.Round(parentFrud.AverageDataValue);
                    double sumValues = Math.Round(parentDataAvg, 0);
                    sumValues += ProcessChildFrudsGetDataSum(ref AllAppHandlesData, processName, repOrInst, app, token);

                    if (sumValues > parentDataAvg)
                    {
                        parentFrud.Data.Clear();
                        parentFrud.Data.Add((float)sumValues);
                    }

                    // Parent's aggregated (summed) spawned process data.
                    ProcessResourceDataReportHealth(
                            parentFrud,
                            app.ErrorOpenFileHandles,
                            app.WarningOpenFileHandles,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                }
            }

            return Task.CompletedTask;
        }

        private double ProcessChildFrudsGetDataSum<T>(
                              ref List<FabricResourceUsageData<T>> fruds,
                              string parentProcessName,
                              ReplicaOrInstanceMonitoringInfo repOrInst,
                              ApplicationInfo app,
                              CancellationToken token) where T : struct
        {
            double sumValues = 0;

            // Child processes (sum)
            if (fruds.Any(x => x.Id.Contains(parentProcessName) && x.Id.Contains("_child")))
            {
                var childFruds = fruds.Where(x => x.Id.Contains(parentProcessName) && x.Id.Contains("_child")).ToList();

                foreach (var frud in childFruds)
                {
                    token.ThrowIfCancellationRequested();

                    sumValues += Math.Round(frud.AverageDataValue, 0);
                    string childProcName = frud.Id.Split("_")[1];
                    int childPid = 0;
                    Process[] ps = null;

                    try
                    {
                        ps = Process.GetProcessesByName(childProcName);
                        childPid = ps[0].Id;
                    }
                    catch (Exception e) when (e is ArgumentException || e is Win32Exception)
                    {

                    }
                    finally
                    {
                        foreach (var proc in ps)
                        {
                            proc?.Dispose();
                        }
                    }

                    if (IsEtwEnabled)
                    {
                        var rawdata = new
                        {
                            ApplicationName = repOrInst.ApplicationName.OriginalString,
                            Description = $"{repOrInst.ServiceName.OriginalString}: child process {childProcName} {frud.Property}.",
                            Metric = frud.Property,
                            NodeName,
                            ObserverName,
                            PartitionId = repOrInst.PartitionId.ToString(),
                            ProcessId = childPid > 0 ? childPid : -1,
                            ReplicaId = repOrInst.ReplicaOrInstanceId.ToString(),
                            ServiceName = repOrInst.ServiceName.OriginalString + "::" + childProcName,
                            Source = ObserverConstants.FabricObserverName,
                            Value = frud.AverageDataValue,
                        };

                        ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, rawdata);
                    }

                    if (IsTelemetryEnabled)
                    {
                        var telemData = new TelemetryData
                        {
                            ApplicationName = repOrInst.ApplicationName.OriginalString,
                            Description = $"{repOrInst.ServiceName.OriginalString}: child process {childProcName} {frud.Property}.",
                            Metric = frud.Property,
                            NodeName = NodeName,
                            ObserverName = ObserverName,
                            PartitionId = repOrInst.PartitionId.ToString(),
                            ProcessId = childPid > 0 ? childPid.ToString() : string.Empty,
                            ReplicaId = repOrInst.ReplicaOrInstanceId.ToString(),
                            ServiceName = repOrInst.ServiceName.OriginalString + "::" + childProcName,
                            Source = ObserverConstants.FabricObserverName,
                            Value = frud.AverageDataValue
                        };

                        _ = TelemetryClient?.ReportMetricAsync(telemData, token);
                    }

                    if (frud.IsUnhealthy(app.MemoryWarningLimitMb))
                    {
                        if (IsEtwEnabled)
                        {
                            var warningdata = new
                            {
                                ApplicationName = repOrInst.ApplicationName.OriginalString,
                                Description = $"{repOrInst.ServiceName.OriginalString}: child process {childProcName} has exceeded supplied threshold for {frud.Property}.",
                                Level = "Warning",
                                Metric = frud.Property,
                                NodeName,
                                ObserverName,
                                PartitionId = repOrInst.PartitionId.ToString(),
                                ProcessId = childPid > 0 ? childPid : -1,
                                ReplicaId = repOrInst.ReplicaOrInstanceId.ToString(),
                                ServiceName = repOrInst.ServiceName.OriginalString + "::" + childProcName,
                                Source = ObserverConstants.FabricObserverName,
                                Value = frud.AverageDataValue
                            };

                            ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, warningdata);
                        }

                        if (IsTelemetryEnabled)
                        {
                            var telemWarnData = new TelemetryData
                            {
                                ApplicationName = repOrInst.ApplicationName.OriginalString,
                                Description = $"{repOrInst.ServiceName.OriginalString}: child process {childProcName} has exceeded supplied threshold for {frud.Property}.",
                                Metric = frud.Property,
                                NodeName = NodeName,
                                ObserverName = ObserverName,
                                PartitionId = repOrInst.PartitionId.ToString(),
                                ProcessId = childPid > 0 ? childPid.ToString() : string.Empty,
                                ReplicaId = repOrInst.ReplicaOrInstanceId.ToString(),
                                ServiceName = repOrInst.ServiceName.OriginalString + "::" + childProcName,
                                Source = ObserverConstants.FabricObserverName,
                                Value = frud.AverageDataValue
                            };

                            _ = TelemetryClient?.ReportHealthAsync(telemWarnData, token);
                        }

                        var healthReport = new Utilities.HealthReport
                        {
                            AppName = repOrInst.ApplicationName,
                            Code = FOErrorWarningCodes.Ok,
                            EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
                            HealthMessage = $"{repOrInst.ServiceName.OriginalString}: child process {childProcName} has exceeded supplied threshold for {frud.Property}.",
                            HealthReportTimeToLive = GetHealthReportTimeToLive(),
                            ReportType = HealthReportType.Application,
                            State = HealthState.Ok,
                            NodeName = NodeName,
                            Observer = ObserverName,
                            Property = frud.Id,
                            ResourceUsageDataProperty = frud.Property,
                            SourceId = $"{ObserverName}({FOErrorWarningCodes.Ok})"
                        };

                        // Generate a Service Fabric Health Report.
                        HealthReporter.ReportHealthToServiceFabric(healthReport);
                    }

                    fruds.Remove(frud);
                }
            }

            return sumValues;
        }

        private static string GetAppNameOrType(ReplicaOrInstanceMonitoringInfo repOrInst)
        {
            // targetType specified as TargetAppType name, which means monitor all apps of specified type.
            var appNameOrType = !string.IsNullOrWhiteSpace(repOrInst.ApplicationTypeName) ? repOrInst.ApplicationTypeName : repOrInst.ApplicationName.OriginalString.Replace("fabric:/", string.Empty);
            return appNameOrType;
        }

        // This runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private async Task<bool> InitializeAsync()
        {
            ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();

            configSettings.Initialize(
                            FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(
                                                                                 ObserverConstants.ObserverConfigurationPackageName)?.Settings,
                                                                                 ConfigurationSectionName,
                                                                                 "AppObserverDataFileName");
            
            // Unit tests may have null path and filename, thus the null equivalence operations.
            var appObserverConfigFileName = Path.Combine(ConfigPackagePath ?? string.Empty, configSettings.AppObserverConfigFileName ?? string.Empty);

            if (!File.Exists(appObserverConfigFileName))
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Information);

                return false;
            }

            await using Stream stream = new FileStream(appObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (stream.Length > 0 && JsonHelper.IsJson<List<ApplicationInfo>>(await File.ReadAllTextAsync(appObserverConfigFileName)))
            {
                userTargetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
            }

            // Are any of the config-supplied apps deployed?.
            if (userTargetList.Count == 0)
            {
                WriteToLogWithLevel(
                     ObserverName,
                     $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                     LogLevel.Information);

                return false;
            }

            // Support for specifying single configuration item for all or * applications.
            if (userTargetList != null && userTargetList.Any(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*"))
            {
                ApplicationInfo application = userTargetList.Find(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

                // Get info for 50 apps at a time that are deployed to the same node this FO instance is running on.
                var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
                {
                    IncludeHealthState = false,
                    MaxResults = 50
                };

                var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                       deployedAppQueryDesc,
                                                                                       ConfigurationSettings.AsyncTimeout,
                                                                                       Token),
                                            Token);

                // DeployedApplicationList is a wrapper around List, but does not support AddRange.. Thus, cast it ToList and add to the temp list, then iterate through it.
                // In reality, this list will never be greater than, say, 1000 apps deployed to a node, but it's a good idea to be prepared since AppObserver supports
                // all-app service process monitoring with a very simple configuration pattern.
                var apps = appList.ToList();

                // The GetDeployedApplicationPagedList api will set a continuation token value if it knows it did not return all the results in one swoop.
                // Check that it is not null, and make a new query passing back the token it gave you.
                while (appList.ContinuationToken != null)
                {
                    Token.ThrowIfCancellationRequested();
                    
                    deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;

                    appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                       deployedAppQueryDesc,
                                                                                       ConfigurationSettings.AsyncTimeout,
                                                                                       Token),
                                            Token);

                    apps.AddRange(appList.ToList());

                    // TODO: Add random wait (ms) impl, include cluster size in calc.
                    await Task.Delay(250, Token).ConfigureAwait(true);
                }

                foreach (var app in apps)
                {
                    Token.ThrowIfCancellationRequested();
 
                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        continue;
                    }

                    // App filtering: AppExcludeList, AppIncludeList. This is only useful when you are observing All/* applications for a range of thresholds.
                    if (!string.IsNullOrWhiteSpace(application.AppExcludeList) && application.AppExcludeList.Contains(app.ApplicationName.OriginalString))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(application.AppIncludeList) && !application.AppIncludeList.Contains(app.ApplicationName.OriginalString))
                    {
                        continue;
                    }

                    // Don't create a brand new entry for an existing (specified in configuration) app target/type. Just update the appConfig instance with data supplied in the All//* apps config entry.
                    // Note that if you supply a conflicting setting (where you specify a threshold for a specific app target config item and also in a global config item), then the target-specific setting will be used.
                    // E.g., if you supply a memoryWarningLimitMb threshold for an app named fabric:/MyApp and also supply a memoryWarningLimitMb threshold for all apps ("targetApp" : "All"),
                    // then the threshold specified for fabric:/MyApp will remain in place for that app target. So, target specificity overrides any global setting.
                    if (userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName))
                    {
                        var existingAppConfig = userTargetList.Find(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

                        if (existingAppConfig == null)
                        {
                            continue;
                        }

                        existingAppConfig.ServiceExcludeList = string.IsNullOrWhiteSpace(existingAppConfig.ServiceExcludeList) && !string.IsNullOrWhiteSpace(application.ServiceExcludeList) ? application.ServiceExcludeList : existingAppConfig.ServiceExcludeList;
                        existingAppConfig.ServiceIncludeList = string.IsNullOrWhiteSpace(existingAppConfig.ServiceIncludeList) && !string.IsNullOrWhiteSpace(application.ServiceIncludeList) ? application.ServiceIncludeList : existingAppConfig.ServiceIncludeList;
                        existingAppConfig.MemoryWarningLimitMb = existingAppConfig.MemoryWarningLimitMb == 0 && application.MemoryWarningLimitMb > 0 ? application.MemoryWarningLimitMb : existingAppConfig.MemoryWarningLimitMb;
                        existingAppConfig.MemoryErrorLimitMb = existingAppConfig.MemoryErrorLimitMb == 0 && application.MemoryErrorLimitMb > 0 ? application.MemoryErrorLimitMb : existingAppConfig.MemoryErrorLimitMb;
                        existingAppConfig.MemoryWarningLimitPercent = existingAppConfig.MemoryWarningLimitPercent == 0 && application.MemoryWarningLimitPercent > 0 ? application.MemoryWarningLimitPercent : existingAppConfig.MemoryWarningLimitPercent;
                        existingAppConfig.MemoryErrorLimitPercent = existingAppConfig.MemoryErrorLimitPercent == 0 && application.MemoryErrorLimitPercent > 0 ? application.MemoryErrorLimitPercent : existingAppConfig.MemoryErrorLimitPercent;
                        existingAppConfig.CpuErrorLimitPercent = existingAppConfig.CpuErrorLimitPercent == 0 && application.CpuErrorLimitPercent > 0 ? application.CpuErrorLimitPercent : existingAppConfig.CpuErrorLimitPercent;
                        existingAppConfig.CpuWarningLimitPercent = existingAppConfig.CpuWarningLimitPercent == 0 && application.CpuWarningLimitPercent > 0 ? application.CpuWarningLimitPercent : existingAppConfig.CpuWarningLimitPercent;
                        existingAppConfig.NetworkErrorActivePorts = existingAppConfig.NetworkErrorActivePorts == 0 && application.NetworkErrorActivePorts > 0 ? application.NetworkErrorActivePorts : existingAppConfig.NetworkErrorActivePorts;
                        existingAppConfig.NetworkWarningActivePorts = existingAppConfig.NetworkWarningActivePorts == 0 && application.NetworkWarningActivePorts > 0 ? application.NetworkWarningActivePorts : existingAppConfig.NetworkWarningActivePorts;
                        existingAppConfig.NetworkErrorEphemeralPorts = existingAppConfig.NetworkErrorEphemeralPorts == 0 && application.NetworkErrorEphemeralPorts > 0 ? application.NetworkErrorEphemeralPorts : existingAppConfig.NetworkErrorEphemeralPorts;
                        existingAppConfig.NetworkWarningEphemeralPorts = existingAppConfig.NetworkWarningEphemeralPorts == 0 && application.NetworkWarningEphemeralPorts > 0 ? application.NetworkWarningEphemeralPorts : existingAppConfig.NetworkWarningEphemeralPorts;
                        existingAppConfig.DumpProcessOnError = application.DumpProcessOnError != existingAppConfig.DumpProcessOnError ? application.DumpProcessOnError : existingAppConfig.DumpProcessOnError;
                        existingAppConfig.ErrorOpenFileHandles = existingAppConfig.ErrorOpenFileHandles == 0 && application.ErrorOpenFileHandles > 0 ? application.ErrorOpenFileHandles : existingAppConfig.ErrorOpenFileHandles;
                        existingAppConfig.WarningOpenFileHandles = existingAppConfig.WarningOpenFileHandles == 0 && application.WarningOpenFileHandles > 0 ? application.WarningOpenFileHandles : existingAppConfig.WarningOpenFileHandles;
                    }
                    else
                    {
                        var appConfig = new ApplicationInfo
                        {
                            TargetApp = app.ApplicationName.OriginalString,
                            TargetAppType = null,
                            AppExcludeList = application.AppExcludeList,
                            AppIncludeList = application.AppIncludeList,
                            ServiceExcludeList = application.ServiceExcludeList,
                            ServiceIncludeList = application.ServiceIncludeList,
                            MemoryWarningLimitMb = application.MemoryWarningLimitMb,
                            MemoryErrorLimitMb = application.MemoryErrorLimitMb,
                            MemoryWarningLimitPercent = application.MemoryWarningLimitPercent,
                            MemoryErrorLimitPercent = application.MemoryErrorLimitPercent,
                            CpuErrorLimitPercent = application.CpuErrorLimitPercent,
                            CpuWarningLimitPercent = application.CpuWarningLimitPercent,
                            NetworkErrorActivePorts = application.NetworkErrorActivePorts,
                            NetworkWarningActivePorts = application.NetworkWarningActivePorts,
                            NetworkErrorEphemeralPorts = application.NetworkErrorEphemeralPorts,
                            NetworkWarningEphemeralPorts = application.NetworkWarningEphemeralPorts,
                            DumpProcessOnError = application.DumpProcessOnError,
                            ErrorOpenFileHandles = application.ErrorOpenFileHandles,
                            WarningOpenFileHandles = application.WarningOpenFileHandles
                        };

                        userTargetList.Add(appConfig);
                    }
                }

                // Remove the All or * config item.
                _ = userTargetList.Remove(application);
                apps.Clear();
                apps = null;
            }

            int settingsFail = 0;

            foreach (var application in userTargetList)
            {
                Token.ThrowIfCancellationRequested();

                Uri appUri = null;

                if (string.IsNullOrWhiteSpace(application.TargetApp) && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                                         FabricServiceContext.ServiceName.ToString(),
                                         ObserverName,
                                         HealthState.Warning,
                                         $"InitializeAsync() | {application.TargetApp}: Required setting, target, is not set.");

                    settingsFail++;
                    continue;
                }
                
                if (!string.IsNullOrWhiteSpace(application.TargetApp))
                {
                    try
                    {
                        if (!application.TargetApp.StartsWith("fabric:/"))
                        {
                            application.TargetApp = application.TargetApp.Insert(0, "fabric:/");
                        }

                        if (application.TargetApp.Contains(" "))
                        {
                            application.TargetApp = application.TargetApp.Replace(" ", string.Empty);
                        }

                        appUri = new Uri(application.TargetApp);
                    }
                    catch (Exception e) when (e is ArgumentException || e is UriFormatException)
                    {
                        HealthReporter.ReportFabricObserverServiceHealth(
                                             FabricServiceContext.ServiceName.ToString(),
                                             ObserverName,
                                             HealthState.Warning,
                                             $"InitializeAsync() | {application.TargetApp}: Invalid TargetApp value. " +
                                             $"Value must be a valid Uri string of format \"fabric:/MyApp\", for example.");

                        settingsFail++;
                        continue;
                    }
                }

                // No required settings supplied for deployed application(s).
                if (settingsFail == userTargetList.Count)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(null, application.TargetAppType).ConfigureAwait(true);
                }
                else
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(appUri).ConfigureAwait(true);
                }
            }

            foreach (var rep in ReplicaOrInstanceList)
            {
                Token.ThrowIfCancellationRequested();
                
                try
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring. https://github.com/gittorre/ContainerObserver
                    using Process p = Process.GetProcessById((int)rep.HostProcessId);

                    if (p.ProcessName == "Fabric")
                    {
                        continue;
                    }

                    ObserverLogger.LogInfo($"Will observe resource consumption by {rep.ServiceName?.OriginalString}({rep.HostProcessId}) on Node {NodeName}.");
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
                {
                }
            }

            return true;
        }

        private async Task MonitorDeployedAppsAsync(CancellationToken token)
        {
            Process parentProc = null;
            Process childProc = null;
            int capacity = ReplicaOrInstanceList.Count;
            AllAppCpuData ??= new List<FabricResourceUsageData<double>>(capacity);
            AllAppMemDataMb ??= new List<FabricResourceUsageData<float>>(capacity);
            AllAppMemDataPercent ??= new List<FabricResourceUsageData<double>>(capacity);
            AllAppTotalActivePortsData ??= new List<FabricResourceUsageData<int>>(capacity);
            AllAppEphemeralPortsData ??= new List<FabricResourceUsageData<int>>(capacity);
            AllAppHandlesData ??= new List<FabricResourceUsageData<float>>(capacity);

            foreach (var repOrInst in ReplicaOrInstanceList)
            {
                token.ThrowIfCancellationRequested();

                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                var cpuUsage = new CpuUsage();
                bool checkCpu = false, checkMemMb = false, checkMemPct = false, checkAllPorts = false, checkEphemeralPorts = false, checkHandles = false;
                var application = deployedTargetList?.Find(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());
                
                List<Process> procTree = null;

                if (application?.TargetApp == null && application?.TargetAppType == null)
                {
                    continue;
                }

                try
                {
                    // App level.
                    parentProc = Process.GetProcessById(parentPid);
                    string parentProcName = parentProc.ProcessName;

                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (parentProcName == "Fabric")
                    {
                        continue;
                    }

                    string appNameOrType = GetAppNameOrType(repOrInst);
                    string id = $"{appNameOrType}:{parentProcName}";

                    if (UseCircularBuffer)
                    {
                        capacity = DataCapacity > 0 ? DataCapacity : 5;
                    }
                    else if (MonitorDuration > TimeSpan.MinValue)
                    {
                        capacity = (int)MonitorDuration.TotalSeconds * 4;
                    }

                    // Add new resource data structures for each app service process where the metric is specified in configuration for related observation.
                    if (AllAppCpuData.All(list => list.Id != id) && (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0))
                    {
                        AllAppCpuData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppCpuData.Any(list => list.Id == id))
                    {
                        checkCpu = true;
                    }

                    if (AllAppMemDataMb.All(list => list.Id != id) && (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0))
                    {
                        AllAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataMb.Any(list => list.Id == id))
                    {
                        checkMemMb = true;
                    }

                    if (AllAppMemDataPercent.All(list => list.Id != id) && (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0))
                    {
                        AllAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataPercent.Any(list => list.Id == id))
                    {
                        checkMemPct = true;
                    }

                    if (AllAppTotalActivePortsData.All(list => list.Id != id) && (application.NetworkErrorActivePorts > 0 || application.NetworkWarningActivePorts > 0))
                    {
                        AllAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id, 1));
                    }

                    if (AllAppTotalActivePortsData.Any(list => list.Id == id))
                    {
                        checkAllPorts = true;
                    }

                    if (AllAppEphemeralPortsData.All(list => list.Id != id) && (application.NetworkErrorEphemeralPorts > 0 || application.NetworkWarningEphemeralPorts > 0))
                    {
                        AllAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id, 1));
                    }

                    if (AllAppEphemeralPortsData.Any(list => list.Id == id))
                    {
                        checkEphemeralPorts = true;
                    }

                    // File Handles (FD on linux)
                    if (AllAppHandlesData.All(list => list.Id != id) && (application.ErrorOpenFileHandles > 0 || application.WarningOpenFileHandles > 0))
                    {
                        AllAppHandlesData.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, id, 1));
                    }

                    if (AllAppHandlesData.Any(list => list.Id == id))
                    {
                        checkHandles = true;
                    }

                    /* CPU and Memory Usage */

                    // Get list of child processes of parentProc should they exist.
                    // In order to provide accurate resource usage of an SF service process we need to also account for
                    // any processes (children) that the service process (parent) created/spawned.
                    procTree = new List<Process>
                    {

                        // Add parent to the process tree list since we want to monitor all processes in the family. If there are no child processes,
                        // then only the parent process will be in this list.
                        parentProc
                    };
                    procTree.AddRange(ProcessInfoProvider.Instance.GetChildProcesses(parentProc));

                    foreach (Process proc in procTree)
                    {
                        // Total TCP ports usage
                        if (checkAllPorts)
                        {
                            // Parent process (the service process)
                            if (proc.ProcessName == parentProcName)
                            {
                                AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == id).Data.Add(OperatingSystemInfoProvider.Instance.GetActiveTcpPortCount(proc.Id, FabricServiceContext));
                            }
                            else
                            {
                                // Children (spawned by the parent service process)
                                if (!AllAppTotalActivePortsData.Any(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child"))
                                {
                                    AllAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, $"{parentProcName}_{proc.ProcessName}_child", capacity, UseCircularBuffer));
                                }
                                AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child").Data.Add(OperatingSystemInfoProvider.Instance.GetActiveTcpPortCount(proc.Id, FabricServiceContext));
                            }
                        }

                        // Ephemeral TCP ports usage
                        if (checkEphemeralPorts)
                        {
                            if (proc.ProcessName == parentProcName)
                            {
                                AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == id).Data.Add(OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(proc.Id, FabricServiceContext));
                            }
                            else
                            {
                                if (!AllAppEphemeralPortsData.Any(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child"))
                                {
                                    AllAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, $"{parentProcName}_{proc.ProcessName}_child", capacity, UseCircularBuffer));
                                }
                                AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child").Data.Add(OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(proc.Id, FabricServiceContext));
                            }
                        }

                        TimeSpan duration = TimeSpan.FromSeconds(1);

                        if (MonitorDuration > TimeSpan.MinValue)
                        {
                            duration = MonitorDuration;
                        }

                        // No need to proceed further if no cpu/mem/file handles thresholds are specified in configuration.
                        if (!checkCpu && !checkMemMb && !checkMemPct && !checkHandles)
                        {
                            continue;
                        }

                        /* Warm up counters. */

                        if (checkCpu)
                        {
                            _ = cpuUsage.GetCpuUsagePercentageProcess(proc);
                        }

                        if (checkHandles)
                        {
                            _ = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(proc.Id, FabricServiceContext);
                        }

                        if (checkMemMb || checkMemPct)
                        {
                            _ = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(proc.Id);
                        }

                        float processMem = 0;

                        if (checkMemMb || checkMemPct)
                        {
                            processMem = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(proc.Id);
                        }

                        if (checkHandles)
                        {
                            float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(proc.Id, FabricServiceContext);

                            if (handles > -1)
                            {
                                if (proc.ProcessName == parentProc.ProcessName)
                                {
                                    AllAppHandlesData.FirstOrDefault(x => x.Id == id).Data.Add(handles);
                                }
                                else
                                {
                                    if (!AllAppHandlesData.Any(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child"))
                                    {
                                        AllAppHandlesData.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, $"{parentProcName}_{proc.ProcessName}_child", capacity, UseCircularBuffer));
                                    }

                                    AllAppHandlesData.FirstOrDefault(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child").Data.Add(handles);
                                }
                            }
                        }

                        timer.Start();

                        while (!proc.HasExited && timer.Elapsed.Seconds <= duration.Seconds)
                        {
                            token.ThrowIfCancellationRequested();

                            if (checkCpu)
                            {
                                // CPU (all cores).
                                double cpu = cpuUsage.GetCpuUsagePercentageProcess(proc);

                                if (cpu >= 0)
                                {
                                    if (cpu > 100)
                                    {
                                        cpu = 100;
                                    }

                                    if (proc.ProcessName == parentProc.ProcessName)
                                    {
                                        AllAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                                    }
                                    else
                                    {
                                        if (!AllAppCpuData.Any(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child"))
                                        {
                                            AllAppCpuData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, $"{parentProcName}_{proc.ProcessName}_child", capacity, UseCircularBuffer));
                                        }

                                        AllAppCpuData.FirstOrDefault(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child").Data.Add(cpu);
                                    }
                                }

                                // Memory (private working set (process)).
                                if (checkMemMb)
                                {
                                    if (proc.ProcessName == parentProcName)
                                    {
                                        AllAppMemDataMb.FirstOrDefault(x => x.Id == id).Data.Add(processMem);
                                    }
                                    else
                                    {
                                        if (!AllAppMemDataMb.Any(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child"))
                                        {
                                            AllAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, $"{parentProcName}_{proc.ProcessName}_child", capacity, UseCircularBuffer));
                                        }

                                        AllAppMemDataMb.FirstOrDefault(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child").Data.Add(processMem);
                                    }
                                }

                                // Memory (percent in use (total)).
                                if (checkMemPct)
                                {
                                    var (TotalMemory, _) = OperatingSystemInfoProvider.Instance.TupleGetTotalPhysicalMemorySizeAndPercentInUse();

                                    if (TotalMemory > 0)
                                    {
                                        double usedPct = Math.Round((double)(processMem * 100) / (TotalMemory * 1024), 2);
                                        if (proc.ProcessName == parentProc.ProcessName)
                                        {
                                            AllAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(Math.Round(usedPct, 1));
                                        }
                                        else
                                        {
                                            if (!AllAppMemDataPercent.Any(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child"))
                                            {
                                                AllAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, $"{parentProcName}_{proc.ProcessName}_child", capacity, UseCircularBuffer));
                                            }

                                            AllAppMemDataPercent.FirstOrDefault(x => x.Id == $"{parentProcName}_{proc.ProcessName}_child").Data.Add(Math.Round(usedPct, 1));
                                        }
                                    }
                                }
                            }

                            await Task.Delay(250, Token);
                        }

                        timer.Stop();
                        timer.Reset();
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    ObserverLogger.LogWarning(
                         $"Handled exception in MonitorDeployedAppsAsync: Process {parentPid} is not running or it's running at a higher privilege than FabricObserver.{Environment.NewLine}" +
                         $"ServiceName: {repOrInst.ServiceName?.OriginalString ?? "unknown"}{Environment.NewLine}Error message: {e.Message}");
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogWarning($"Unhandled exception in MonitorDeployedAppsAsync:{Environment.NewLine}{e}");

                    // Fix the bug..
                    throw;
                }
                finally
                {
                    if (procTree != null)
                    {
                        foreach (var p in procTree)
                        {
                            p?.Dispose();
                        }
                    }
                }
            }

            try
            {
                ProcessInfoProvider.Instance.Dispose();
            }
            catch (Exception e)
            {
                ObserverLogger.LogWarning($"Can't dispose ProcessInfoProvider.Instance:{Environment.NewLine}{e}");
            }
        }

        private async Task SetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            var deployedApps = new List<DeployedApplication>();

            if (applicationNameFilter != null)
            {
                var app = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, applicationNameFilter).ConfigureAwait(true);
                deployedApps.AddRange(app.ToList());
            }
            else if (!string.IsNullOrWhiteSpace(applicationType))
            {
                // There is no typename filter (unfortunately), so do a paged query for app data and then filter on supplied typename.
                var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
                {
                    IncludeHealthState = false,
                    MaxResults = 50
                };

                var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                               deployedAppQueryDesc,
                                                                               ConfigurationSettings.AsyncTimeout,
                                                                               Token),
                                    Token);

                deployedApps = appList.ToList();

                while (appList.ContinuationToken != null)
                {
                    Token.ThrowIfCancellationRequested();

                    deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;

                    appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                               deployedAppQueryDesc,
                                                                               ConfigurationSettings.AsyncTimeout,
                                                                               Token),
                                    Token);

                    deployedApps.AddRange(appList.ToList());
                    await Task.Delay(250, Token).ConfigureAwait(true);
                }

                deployedApps = deployedApps.Where(a => a.ApplicationTypeName == applicationType).ToList();
            }

            foreach (var deployedApp in deployedApps)
            {
                Token.ThrowIfCancellationRequested();

                string[] filteredServiceList = null;

                // Filter service list if ServiceExcludeList/ServiceIncludeList config setting is non-empty.
                var serviceFilter = userTargetList.Find(x => (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                                || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())
                                                                && (!string.IsNullOrWhiteSpace(x.ServiceExcludeList) || !string.IsNullOrWhiteSpace(x.ServiceIncludeList)));

                ServiceFilterType filterType = ServiceFilterType.None;
                
                if (serviceFilter != null)
                {
                    if (!string.IsNullOrWhiteSpace(serviceFilter.ServiceExcludeList))
                    {
                        filteredServiceList = serviceFilter.ServiceExcludeList.Replace(" ", string.Empty).Split(',');
                        filterType = ServiceFilterType.Exclude;
                    }
                    else if (!string.IsNullOrWhiteSpace(serviceFilter.ServiceIncludeList))
                    {
                        filteredServiceList = serviceFilter.ServiceIncludeList.Replace(" ", string.Empty).Split(',');
                        filterType = ServiceFilterType.Include;
                    }
                }

                var replicasOrInstances = await GetDeployedPrimaryReplicaAsync(
                                                   deployedApp.ApplicationName,
                                                   filteredServiceList,
                                                   filterType,
                                                   applicationType).ConfigureAwait(true);

                ReplicaOrInstanceList.AddRange(replicasOrInstances);

                deployedTargetList.AddRange(userTargetList.Where(
                                            x => (x.TargetApp != null || x.TargetAppType != null)
                                                 && (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                     || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())));
            }

            deployedApps.Clear();
            deployedApps = null;
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedPrimaryReplicaAsync(
                                                                     Uri appName,
                                                                     string[] serviceFilterList = null,
                                                                     ServiceFilterType filterType = ServiceFilterType.None,
                                                                     string appTypeName = null)
        {
            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName),
                                                Token);

            var replicaMonitoringList = new List<ReplicaOrInstanceMonitoringInfo>(deployedReplicaList.Count);

            SetInstanceOrReplicaMonitoringList(
               appName,
               serviceFilterList,
               filterType,
               appTypeName,
               deployedReplicaList,
               ref replicaMonitoringList);

            return replicaMonitoringList;
        }

        private void SetInstanceOrReplicaMonitoringList(
                        Uri appName,
                        string[] filterList,
                        ServiceFilterType filterType,
                        string appTypeName,
                        DeployedServiceReplicaList deployedReplicaList,
                        ref List<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            foreach (var deployedReplica in deployedReplicaList)
            {
                Token.ThrowIfCancellationRequested();

                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica {ReplicaRole: ReplicaRole.Primary} statefulReplica:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    continue;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ServiceName = statefulReplica.ServiceName
                        };
                        break;
                    }
                    case DeployedStatelessServiceInstance statelessInstance:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    continue;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ServiceName = statelessInstance.ServiceName
                        };
                        break;
                    }
                }

                if (replicaInfo != null)
                {
                    replicaMonitoringList.Add(replicaInfo);
                }
            }
        }

        private void CleanUp()
        {
            deployedTargetList?.Clear();
            deployedTargetList = null;

            userTargetList?.Clear();
            userTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            if (AllAppCpuData != null && !AllAppCpuData.Any(frud => frud.ActiveErrorOrWarning))
            {
                AllAppCpuData?.Clear();
                AllAppCpuData = null;
            }

            if (AllAppEphemeralPortsData != null && !AllAppEphemeralPortsData.Any(frud => frud.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsData?.Clear();
                AllAppEphemeralPortsData = null;
            }

            if (AllAppHandlesData != null && !AllAppHandlesData.Any(frud => frud.ActiveErrorOrWarning))
            {
                AllAppHandlesData?.Clear();
                AllAppHandlesData = null;
            }

            if (AllAppMemDataMb != null && !AllAppMemDataMb.Any(frud => frud.ActiveErrorOrWarning))
            {
                AllAppMemDataMb?.Clear();
                AllAppMemDataMb = null;
            }

            if (AllAppMemDataPercent != null && !AllAppMemDataPercent.Any(frud => frud.ActiveErrorOrWarning))
            {
                AllAppMemDataPercent?.Clear();
                AllAppMemDataPercent = null;
            }

            if (AllAppTotalActivePortsData != null && !AllAppTotalActivePortsData.Any(frud => frud.ActiveErrorOrWarning))
            {
                AllAppTotalActivePortsData?.Clear();
                AllAppTotalActivePortsData = null;
            }
        }

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!EnableCsvLogging)
            {
                return;
            }

            // CPU Time
            if (AllAppCpuData.Any(x => x.Id == appName))
            {
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalCpuTime,
                    "Average",
                    Math.Round(AllAppCpuData.Find(x => x.Id == appName).AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalCpuTime,
                    "Peak",
                    Math.Round(AllAppCpuData.FirstOrDefault(x => x.Id == appName).MaxDataValue));
            }

            // Memory - MB
            if (AllAppMemDataMb.Any(x => x.Id == appName))
            {
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionMb,
                    "Average",
                    Math.Round(AllAppMemDataMb.FirstOrDefault(x => x.Id == appName).AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionMb,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataMb.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppMemDataPercent.Any(x => x.Id == appName))
            {
                CsvFileLogger.LogData(
                   fileName,
                   appName,
                   ErrorWarningProperty.TotalMemoryConsumptionPct,
                   "Average",
                   Math.Round(AllAppMemDataPercent.FirstOrDefault(x => x.Id == appName).AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionPct,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataPercent.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppTotalActivePortsData.Any(x => x.Id == appName))
            {
                // Network
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalActivePorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppEphemeralPortsData.Any(x => x.Id == appName))
            {
                // Network
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalEphemeralPorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppHandlesData.Any(x => x.Id == appName))
            {
                // Handles
                CsvFileLogger.LogData(
                     fileName,
                     appName,
                     ErrorWarningProperty.TotalFileHandles,
                     "Total",
                     Math.Round(AllAppHandlesData.FirstOrDefault(x => x.Id == appName).MaxDataValue));
            }

            DataTableFileLogger.Flush();
        }
    }
}