﻿using Castle.DynamicProxy;
using Microsoft.Azure.TypeEdge.Attributes;
using Microsoft.Azure.TypeEdge.Modules;
using Microsoft.Azure.TypeEdge.Modules.Endpoints;
using Microsoft.Azure.TypeEdge.Twins;
using Microsoft.Azure.TypeEdge.Volumes;
using System;
using System.Globalization;
using System.Reflection;

namespace Microsoft.Azure.TypeEdge.Proxy
{
    internal class ModuleProxyBase : TypeModule, IInterceptor
    {
        private readonly Type _type;

        public ModuleProxyBase(Type type)
        {
            _type = type;
            if (!(_type.GetCustomAttribute(typeof(TypeModuleAttribute), true) is TypeModuleAttribute))
                throw new ArgumentException($"{_type.Name} has no TypeModule annotation");
            if (!_type.IsInterface)
                throw new ArgumentException($"{_type.Name} needs to be an interface");
        }

        public override string Name
        {
            get
            {
                return _type.GetModuleName();
            }
        }

        public void Intercept(IInvocation invocation)
        {
            if (!invocation.Method.ReturnType.IsGenericType)
                return;
            var genericDef = invocation.Method.ReturnType.GetGenericTypeDefinition();
            if (!genericDef.IsAssignableFrom(typeof(Input<>)) &&
                !genericDef.IsAssignableFrom(typeof(Output<>)) &&
                !genericDef.IsAssignableFrom(typeof(ModuleTwin<>)) &&
                !genericDef.IsAssignableFrom(typeof(Volume<>)))
                return;
            var value = Activator.CreateInstance(
                genericDef.MakeGenericType(invocation.Method.ReturnType.GenericTypeArguments),
                invocation.Method.Name.Replace("get_", ""), this);
            invocation.ReturnValue = value;
        }
    }
}