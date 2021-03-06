//------------------------------------------------------------------------------
// <copyright file="IInternalConfigRoot.cs" company="Microsoft">
//     
//      Copyright (c) 2006 Microsoft Corporation.  All rights reserved.
//     
//      The use and distribution terms for this software are contained in the file
//      named license.txt, which can be found in the root of this distribution.
//      By using this software in any fashion, you are agreeing to be bound by the
//      terms of this license.
//     
//      You must not remove this notice, or any other, from this software.
//     
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.IO;
using System.Security;
using System.Collections.Specialized;
using System.Configuration;
using ClassConfiguration=System.Configuration.Configuration;

//
// This file contains most of the interfaces that allow System.Web, Venus, and 
// Whitehorse to customize configuration in some way.
//
// The goal of the design of customization is to only require other MS assemblies
// to create an instance of an internal object via Activator.CreateInstance(), and then
// use these objects through *public* System.Configuration.Internal interfaces. 
// We do not want extenders to have to use reflection to call a method - it is slow,
// not typesafe, and more difficult to promote correct use of the internal object.
//
namespace System.Configuration.Internal {

    // Exposes the functionality of InternalConfigRoot.
    [System.Runtime.InteropServices.ComVisible(false)]
    public interface IInternalConfigRoot {
        void                                Init(IInternalConfigHost host, bool isDesignTime);
        bool                                IsDesignTime {get;}
        event InternalConfigEventHandler    ConfigChanged;
        event InternalConfigEventHandler    ConfigRemoved;
        object                              GetSection(string section, string configPath);

        // Get the configPath of the nearest ancestor that has the config data
        string                              GetUniqueConfigPath(string configPath);   
        
        IInternalConfigRecord               GetUniqueConfigRecord(string configPath); 
        IInternalConfigRecord               GetConfigRecord(string configPath);       
        void                                RemoveConfig(string configPath);          
    }
}
