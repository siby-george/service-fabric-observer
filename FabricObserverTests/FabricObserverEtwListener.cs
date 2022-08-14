﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserverTests
{
    using FabricObserver.Observers.Utilities;
    using FabricObserver.Observers.Utilities.Telemetry;
    using System.Diagnostics.Tracing;

    /// <summary>
    /// Class that reads ETW events generated by FabricObserver.
    /// </summary>
    internal class FabricObserverEtwListener : EventListener
    {
        private readonly object lockObj = new object();
        private readonly Logger logger;
        internal readonly EtwEventConverter foEtwConverter;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserverEtwListener"/> class.
        /// </summary>
        /// <param name="observerLogger"> Logger for the observer. </param>
        internal FabricObserverEtwListener(Logger observerLogger)
        {
            logger = observerLogger;
            foEtwConverter = new EtwEventConverter(logger);
            StartFoEventSourceListener();
            logger.LogInfo($"FabricObserverEtwListenerInfo: Started FabricObserverEtwListener.");
        }

        /// <summary>
        /// Starts event listening on FO's ServiceEventSource object. 
        /// </summary>
        /// <param name="eventSource">The event source</param>
        protected void StartFoEventSourceListener()
        {
            ServiceEventSource.Current ??= new ServiceEventSource();
            EnableEvents(ServiceEventSource.Current, EventLevel.Informational | EventLevel.Warning | EventLevel.Error);
            logger.LogInfo($"FabricObserverEtwListenerInfo: Enabled Events.");
        }

        /// <summary>
        /// Called whenever an event has been written by an event source for which the event listener has enabled events.
        /// </summary>
        /// <param name="eventData">Instance of information associated with the event dispatched for fabric observer event listener.</param>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (lockObj)
            {
                logger.LogInfo($"FabricObserverEtwListenerInfo: Using event data to parse to telemetry.");
                
                // Parse the event data as TelemetryData and publish as Azure metrics.
                foEtwConverter.EventDataToTelemetryData(eventData);
            }
        }

        /// <summary>
        /// Dispose event source object.
        /// </summary>
        public override void Dispose()
        {
            DisableEvents(ServiceEventSource.Current);
            base.Dispose();
        }
    }
}