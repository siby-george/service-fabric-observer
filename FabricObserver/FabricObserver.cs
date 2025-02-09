﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Runtime;

namespace FabricObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricObserver : StatelessService
    {
        private readonly FabricClient fabricClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserver"/> class.
        /// </summary>
        /// <param name="context">StatelessServiceContext.</param>
        public FabricObserver(StatelessServiceContext context)
            : base(context)
        {
            fabricClient = new FabricClient();
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        /// <returns>a Task.</returns>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);

            await using ServiceProvider serviceProvider = services.BuildServiceProvider();
            using var observerManager = new ObserverManager(serviceProvider, fabricClient, cancellationToken);
            await observerManager.StartObserversAsync();
        }

        /// <summary>
        /// This function will add observer instances, both static (so, part of the FO impl) and dynamic (so, observer plugin dlls).
        /// </summary>
        /// <param name="services">ServiceCollection collection instance.</param>
        private void ConfigureServices(IServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), s => new AppObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new AzureStorageUploadObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new CertificateObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new ContainerObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new DiskObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new FabricSystemObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new NetworkObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new NodeObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new OSObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new SFConfigurationObserver(fabricClient, Context));
            _ = services.AddSingleton(typeof(StatelessServiceContext), Context);

            LoadObserversFromPlugins(services);
        }

        /// <summary>
        /// This function will load observer plugin dlls from PackageRoot/Data/Plugins folder and add them to the ServiceCollection instance.
        /// </summary>
        /// <param name="services"></param>
        private void LoadObserversFromPlugins(IServiceCollection services)
        {
            string pluginsDir = Path.Combine(Context.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);

            if (pluginDlls.Length == 0)
            {
                return;
            }

            PluginLoader[] pluginLoaders = new PluginLoader[pluginDlls.Length];
            Type[] sharedTypes = { typeof(FabricObserverStartupAttribute), typeof(IFabricObserverStartup), typeof(IServiceCollection) };

            for (int i = 0; i < pluginDlls.Length; ++i)
            {
                string dll = pluginDlls[i];
                PluginLoader loader = PluginLoader.CreateFromAssemblyFile(dll, sharedTypes, a => a.IsUnloadable = false);
                pluginLoaders[i] = loader;
            }

            for (int i = 0; i < pluginLoaders.Length; ++i)
            {
                var pluginLoader = pluginLoaders[i];
                Assembly pluginAssembly;

                try
                {
                    // If your plugin has native library dependencies (that's fine), then we will land in the catch (BadImageFormatException).
                    // This is by design. The Managed FO plugin assembly will successfully load, of course.
                    pluginAssembly = pluginLoader.LoadDefaultAssembly();
                    FabricObserverStartupAttribute[] startupAttributes = pluginAssembly.GetCustomAttributes<FabricObserverStartupAttribute>().ToArray();

                    for (int j = 0; j < startupAttributes.Length; ++j)
                    {
                        object startupObject = Activator.CreateInstance(startupAttributes[j].StartupType);

                        if (startupObject is IFabricObserverStartup fabricObserverStartup)
                        {
                            fabricObserverStartup.ConfigureServices(services, fabricClient, Context);
                        }
                        else
                        {
                            // This will bring down FO, which it should: This means your plugin is not supported. Fix your bug.
                            throw new InvalidOperationException($"{startupAttributes[j].StartupType.FullName} must implement IFabricObserverStartup.");
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is BadImageFormatException || e is IOException)
                {
                    continue;
                }
            }
        }
    }
}