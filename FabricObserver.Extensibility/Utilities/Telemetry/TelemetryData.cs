﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// This is for backwards compatitibilty with the older telemetry data model. There are now three types of telemetry data based on observer:
    /// ServiceTelemetryData (AppObserver/ContainerObserver/FabricSystemObserver), DiskTelemetryData (DiskObserver), NodeTelemetryData (NodeObserver).
    /// </summary>
    [EventData]
    [Serializable]
    public class TelemetryData : TelemetryDataBase
    {
        [EventField]
        public string ApplicationName
        {
            get; set;
        }
        [EventField]
        public string ApplicationType
        {
            get; set;
        }
        [EventField]
        public string ApplicationTypeVersion
        {
            get; set;
        }
        [EventField]
        public string ContainerId
        {
            get; set;
        }
        [EventField]
        public Guid? PartitionId
        {
            get; set;
        }
        [EventField]
        public long ProcessId
        {
            get; set;
        }
        [EventField]
        public string ProcessName
        {
            get; set;
        }
        [EventField]
        public string ProcessStartTime
        {
            get; set;
        }
        [EventField]
        public long ReplicaId
        {
            get; set;
        }
        [EventField]
        public string ReplicaRole
        {
            get; set;
        }
        [EventField]
        public bool RGMemoryEnabled
        {
            get; set;
        }

        /* TODO..
        [EventField]
        public bool RGCpuEnabled
        {
            get; set;
        }
        */

        [EventField]
        public double RGAppliedMemoryLimitMb
        {
            get; set;
        }
        [EventField]
        public string ServiceKind
        {
            get; set;
        }
        [EventField]
        public string ServiceName
        {
            get; set;
        }
        [EventField]
        public string ServiceTypeName
        {
            get; set;
        }
        [EventField]
        public string ServiceTypeVersion
        {
            get; set;
        }
        [EventField]
        public string ServicePackageActivationMode
        {
            get; set;
        }

        [JsonConstructor]
        public TelemetryData() : base()
        {

        }
    }
}
