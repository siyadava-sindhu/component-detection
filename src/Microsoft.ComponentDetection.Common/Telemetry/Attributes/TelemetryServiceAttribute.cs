﻿namespace Microsoft.ComponentDetection.Common.Telemetry.Attributes;
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TelemetryServiceAttribute : Attribute
{
    public TelemetryServiceAttribute(string serviceType) => this.ServiceType = serviceType;

    public string ServiceType { get; }
}
