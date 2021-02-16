﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Linq;
using System.Threading;
using ClusterObserver.Interfaces;
using ClusterObserver.Utilities.Telemetry;

namespace ClusterObserver.Utilities
{
    public class ConfigSettings
    {
        private readonly FabricClient fabricClient;
        private readonly CancellationToken token;

        private ConfigurationSettings Settings
        {
            get; set;
        }

        private ConfigurationSection Section
        {
            get; set;
        }

        public TimeSpan RunInterval
        {
            get; set;
        }

        public bool IsEnabled
        {
            get; set;
        } = true;

        public bool EnableVerboseLogging
        {
            get; set;
        }

        public bool TelemetryEnabled
        {
            get; set;
        }

        public ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        public bool EmitWarningDetails
        {
            get; set;
        }

        public TimeSpan AsyncTimeout
        {
            get; set;
        }

        public TimeSpan MaxTimeNodeStatusNotOk
        {
            get; set;
        } = TimeSpan.FromHours(2.0);

        public ConfigSettings(
            ConfigurationSettings settings,
            string observerConfiguration,
            FabricClient fabricClient,
            CancellationToken token)
        {
            Settings = settings;
            Section = settings?.Sections[observerConfiguration];
            this.fabricClient = fabricClient;
            this.token = token;

            UpdateConfigSettings();
        }

        public void UpdateConfigSettings(ConfigurationSettings settings = null)
        {
            if (settings != null)
            {
                Settings = settings;
            }

            // Observer enabled?
            if (bool.TryParse(
                GetConfigSettingValue(
                ObserverConstants.ObserverEnabled),
                out bool enabled))
            {
                IsEnabled = enabled;
            }

            
            // Verbose logging?
            if (bool.TryParse(
                GetConfigSettingValue(
                ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                EnableVerboseLogging = enableVerboseLogging;
            }

            // RunInterval?
            if (TimeSpan.TryParse(
                GetConfigSettingValue(
                ObserverConstants.ObserverRunIntervalParameterName),
                out TimeSpan runInterval))
            {
                RunInterval = runInterval;
            }

            // Async cluster operation timeout setting.
            if (int.TryParse(
                GetConfigSettingValue(
                ObserverConstants.AsyncOperationTimeoutSeconds),
                out int asyncOpTimeoutSeconds))
            {
                AsyncTimeout = TimeSpan.FromSeconds(asyncOpTimeoutSeconds);
            }

            // Get ClusterObserver settings (specified in PackageRoot/Config/Settings.xml).
            if (bool.TryParse(
                GetConfigSettingValue(
                    ObserverConstants.EmitHealthWarningEvaluationConfigurationSetting),
                    out bool emitWarningDetails))
            {
                EmitWarningDetails = emitWarningDetails;
            }

            if (TimeSpan.TryParse(
                GetConfigSettingValue(
                   ObserverConstants.MaxTimeNodeStatusNotOkSetting),
                   out TimeSpan maxTimeNodeStatusNotOk))
            {
                MaxTimeNodeStatusNotOk = maxTimeNodeStatusNotOk;
            }

            // Observer telemetry enabled?
            if (bool.TryParse(
                GetConfigSettingValue(
                ObserverConstants.EnableTelemetry),
                out bool telemetryEnabled))
            {
                TelemetryEnabled = telemetryEnabled;
            }

            if (TelemetryEnabled)
            {
                string telemetryProviderType = GetConfigSettingValue(ObserverConstants.TelemetryProviderType);

                if (string.IsNullOrWhiteSpace(telemetryProviderType))
                {
                    TelemetryEnabled = false;
                    return;
                }

                if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
                {
                    TelemetryEnabled = false;
                    return;
                }

                switch (telemetryProvider)
                {
                    case TelemetryProviderType.AzureLogAnalytics:

                        var logAnalyticsLogType =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter) ?? "Application";

                        var logAnalyticsSharedKey =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter);

                        var logAnalyticsWorkspaceId =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                        if (string.IsNullOrWhiteSpace(logAnalyticsSharedKey) || string.IsNullOrWhiteSpace(logAnalyticsWorkspaceId))
                        {
                            TelemetryEnabled = false;
                            return;
                        }
                        
                        TelemetryClient = new LogAnalyticsTelemetry(
                            logAnalyticsWorkspaceId,
                            logAnalyticsSharedKey,
                            logAnalyticsLogType,
                            this.fabricClient,
                            this.token);
                        
                        break;

                    case TelemetryProviderType.AzureApplicationInsights:

                        string aiKey = GetConfigSettingValue(ObserverConstants.AiKey);

                        if (string.IsNullOrWhiteSpace(aiKey))
                        {
                            TelemetryEnabled = false;
                            return;
                        }
                         
                        TelemetryClient = new AppInsightsTelemetry(aiKey);
                        
                        break;
                }
            }
        }

        private string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = Settings;

                if (configSettings == null || string.IsNullOrEmpty(Section.Name))
                {
                    return null;
                }

                if (Section == null)
                {
                    return null;
                }

                ConfigurationProperty parameter = null;

                if (Section.Parameters.Any(p => p.Name == parameterName))
                {
                    parameter = Section.Parameters[parameterName];
                }

                if (parameter == null)
                {
                    return null;
                }

                return parameter.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {

            }

            return null;
        }
    }
}
