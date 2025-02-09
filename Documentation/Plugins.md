## How to implement an observer plugin using FO's extensibility model

This document is a simple overview of how to get started with building an observer plugin. Also, for a more advanced sample, please see [ContainerObserver](https://github.com/gittorre/containerobserver) reference project (ContainerObserver is a part of FO (since version 3.1.17)).

Note: The plugin model depends on the following packages, which **must have the same versions in both your plugin project and FabricObserver**:

Current: 

**Microsoft.Extensions.DependencyInjection, Version 5.0.1**  
**Microsoft.Extensions.DependencyInjection.Abstractions, Version 5.0.0**  (Observer plugins must employ this package, which must be the same version as FabricObserver's referenced package)  

#### Steps 

FabricObserver is a .NET Core 3.1 application. A FabricObserver plugin is a .NET Standard 2.0 library that consumes FabricObserver's public API, which is housed inside a .NET Standard 2.0 library, FabricObserver.Extensibility.dll. 
Your plugin must be built as a .NET Standard 2.0 library.

Install [.Net Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1).

Create a new .NET Standard 2.0 library project, install the nupkg you need for your target OS (Linux (Ubuntu) or Windows):  

You can find the Microsoft-signed packages in the nuget.org gallery [here](https://www.nuget.org/profiles/ServiceFabricApps) or just run this in the package manager console:

```
Install-Package Microsoft.ServiceFabricApps.FabricObserver.Windows.SelfContained -Version 3.1.26   

or for Linux:

Install-Package Microsoft.ServiceFabricApps.FabricObserver.Linux.SelfContained -Version 3.1.26
```

Note:

FrameworkDependent = Requires that .NET Core 3.1 is already installed on target machine.  

SelfContained = Includes all the binaries necessary for running .NET Core 3.1 applications on target machine. ***This is what you will want to use for your Azure deployments.***

**Plugins and Dependencies** 

- You MUST place all plugin dependency libraries in the same folder as your plugin dll.
- Plugins (and their dependencies) can live in child folders in the Plugins directory, which will keep things cleaner for folks with multiple plugins.

The Plugins folder/file structure MUST be: 

- Config/Data/Plugins/MyPlugin/MyPlugin.dll (required), MyPlugin.pdb (optional), [ALL of MyPlugin.dll's private dependencies] (required) 

OR 

- Config/Data/Plugins/MyPlugin.dll (required), MyPlugin.pdb(optional), [ALL of MyPlugin.dll's private dependencies] (required).  

A private plugin dependency is any file (typically a dll) that you reference in your plugin project that is not already referenced by FabricObserver. 

So, things like Nuget packages or Project References or COM References that are only used by your plugin. It is important to stress that if a dependency dll has dependencies, then you MUST also place those in the plugin's directory.
When you build your plugin project you will see the dependencies (if any) in the bin folder. Make sure ALL of them are placed into FO's Plugins folder or your plugin will not work. 

**Build and Publish**  

- Write your observer plugin!

- Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.

- Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Application Parameter Updates for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)

- Deploy FabricObserver to your cluster. Your new observer will be managed and run just like any other observer.

#### Due to the complexity of unloading plugins at runtime, in order to add or update a plugin, you must redeploy FabricObserver. The problem is easier to solve for new plugins, as this could be done via a Data configuration update, but we have not added support for this yet.
