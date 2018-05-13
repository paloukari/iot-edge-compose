﻿using System;
using System.Threading.Tasks;

namespace Microsoft.Azure.IoT.TypeEdge.Modules
{
    public abstract class Endpoint
    {
        public string Name { get; set; }
        public abstract string RouteName { get; }
        internal EdgeModule Module { get; set; }

        public Endpoint(string name, EdgeModule module)
        {
            Name = name;
            Module = module;
        }
        
    }
}