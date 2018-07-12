﻿using Microsoft.Azure.Devices.Shared;

namespace TypeEdge.Twins
{
    public interface IModuleTwin
    {
        Twin LastKnownTwin { get; set; }
        Twin GetDesiredTwin(string name);
        Twin GetReportedTwin(string name);
        void SetTwin(string name, Twin twin);
    }
}