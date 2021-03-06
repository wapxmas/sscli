//------------------------------------------------------------------------------
// <copyright file="ClientConfigPaths.cs" company="Microsoft">
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

namespace System.Configuration {
    using System;
    using System.Collections;
    using System.IO;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Runtime.Serialization.Formatters.Binary;
    using System.Security;
    using System.Security.Cryptography;
    using System.Security.Policy;
    using System.Security.Permissions;
    using System.Text;
    using System.Globalization;
    using Microsoft.Win32;

    class ClientConfigPaths {
        internal const string       UserConfigFilename = "user.config";
        
        const string                ClickOnceDataDirectory = "DataDirectory";
        const string                ConfigExtension = ".config";
        const int                   MAX_PATH = 260;
        const int                   MAX_LENGTH_TO_USE = 25;
        const string                FILE_URI_LOCAL = "file:///";
        const string                FILE_URI_UNC = "file://";
        const string                FILE_URI = "file:";
        const string                HTTP_URI = "http://";
        const string                StrongNameDesc = "StrongName";
        const string                UrlDesc = "Url";
        const string                PathDesc = "Path";

        static Char[] s_Base32Char   = {
                'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 
                'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p',
                'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 
                'y', 'z', '0', '1', '2', '3', '4', '5'};

        static  volatile ClientConfigPaths  s_current;
        static  volatile bool               s_currentIncludesUserConfig;
        static  SecurityPermission          s_serializationPerm;
        static  SecurityPermission          s_controlEvidencePerm;

        bool    _hasEntryAssembly;
        bool    _includesUserConfig;
        string  _applicationUri;
        string  _applicationConfigUri;
        string  _roamingConfigDirectory;
        string  _roamingConfigFilename;
        string  _localConfigDirectory;
        string  _localConfigFilename;
        string  _companyName;
        string  _productName;
        string  _productVersion;

        
        [FileIOPermission(SecurityAction.Assert, AllFiles=FileIOPermissionAccess.PathDiscovery | FileIOPermissionAccess.Read)]
        [SecurityPermission(SecurityAction.Assert, UnmanagedCode=true)]
        private ClientConfigPaths(string exePath, bool includeUserConfig) {

            _includesUserConfig = includeUserConfig;

            Assembly    exeAssembly = null;
            string      applicationUri = null;
            string      applicationFilename = null;
            
            // get the assembly and applicationUri for the file
            if (exePath == null) {
                // First check if a configuration file has been set for this app domain. If so, we will use that.
                // The CLR would already have normalized this, so no further processing necessary.
                AppDomain domain = AppDomain.CurrentDomain;
                AppDomainSetup setup = domain.SetupInformation;
                _applicationConfigUri = setup.ConfigurationFile;

                // Now figure out the application path.
                exeAssembly = Assembly.GetEntryAssembly();
                if (exeAssembly != null) {
                    _hasEntryAssembly = true;
                    applicationUri = exeAssembly.CodeBase;

                    bool isFile = false;

                    // If it is a local file URI, convert it to its filename, without invoking Uri class.
                    // example: "file:///C:/WINNT/Microsoft.NET/Framework/v2.0.x86fre/csc.exe"
                    if (StringUtil.StartsWithIgnoreCase(applicationUri, FILE_URI_LOCAL)) {
                        isFile = true;
                        applicationUri = applicationUri.Substring(FILE_URI_LOCAL.Length);
                    }
                    // If it is a UNC file URI, convert it to its filename, without invoking Uri class.
                    // example: "file://server/share/csc.exe"
                    else if (StringUtil.StartsWithIgnoreCase(applicationUri, FILE_URI_UNC)) {
                        isFile = true;
                        applicationUri = applicationUri.Substring(FILE_URI.Length);
                    }

                    if (isFile) {
                        applicationUri = applicationUri.Replace('/', '\\');
                        applicationFilename = applicationUri;
                    }
                    else {
                        applicationUri = exeAssembly.EscapedCodeBase;
                    }
                }
                else {
                    StringBuilder sb = new StringBuilder(MAX_PATH);
                    UnsafeNativeMethods.GetModuleFileName(new HandleRef(null, IntPtr.Zero), sb, sb.Capacity);
                    applicationUri = Path.GetFullPath(sb.ToString());
                    applicationFilename = applicationUri;
                }
            }
            else {
                applicationUri = Path.GetFullPath(exePath);
                if (!FileUtil.FileExists(applicationUri, false))
                    throw ExceptionUtil.ParameterInvalid("exePath");

                applicationFilename = applicationUri;
            }

            // Fallback if we haven't set the app config file path yet.
            if (_applicationConfigUri == null) {
                _applicationConfigUri = applicationUri + ConfigExtension;
            }

            // Set application path
            _applicationUri = applicationUri;

            // In the case when exePath was explicitly supplied, we will not be able to 
            // construct user.config paths, so quit here.
            if (exePath != null) {
                return;
            }

            // Skip expensive initialization of user config file information if requested.
            if (!_includesUserConfig) {
                return;
            }

            bool isHttp = StringUtil.StartsWithIgnoreCase(_applicationConfigUri, HTTP_URI);

            SetNamesAndVersion(applicationFilename, exeAssembly, isHttp);

            // Check if this is a clickonce deployed application. If so, point the user config
            // files to the clickonce data directory.
            if (this.IsClickOnceDeployed(AppDomain.CurrentDomain)) {
                string dataPath = AppDomain.CurrentDomain.GetData(ClickOnceDataDirectory) as string;
                string versionSuffix = Validate(_productVersion, false);

                // NOTE: No roaming config for clickonce - not supported.
                if (Path.IsPathRooted(dataPath)) {
                    _localConfigDirectory = CombineIfValid(dataPath, versionSuffix);
                    _localConfigFilename  = CombineIfValid(_localConfigDirectory, UserConfigFilename);
                }

            }
            else if (!isHttp) {
                // If we get the config from http, we do not have a roaming or local config directory,
                // as it cannot be edited by the app in those cases because it does not have Full Trust.
                
                // suffix for user config paths

                string part1 = Validate(_companyName, true);

                string validAppDomainName = Validate(AppDomain.CurrentDomain.FriendlyName, true);
                string applicationUriLower = !String.IsNullOrEmpty(_applicationUri) ? _applicationUri.ToLower(CultureInfo.InvariantCulture) : null;
                string namePrefix = !String.IsNullOrEmpty(validAppDomainName) ? validAppDomainName : Validate(_productName, true);
                string hashSuffix = string.Empty;
                
                string part2 = (!String.IsNullOrEmpty(namePrefix) && !String.IsNullOrEmpty(hashSuffix)) ? namePrefix + hashSuffix : null;
                
                string part3 = Validate(_productVersion, false);

                string dirSuffix = CombineIfValid(CombineIfValid(part1, part2), part3);
            }
        }

        internal static ClientConfigPaths GetPaths(string exePath, bool includeUserConfig) {
            ClientConfigPaths result = null;

            if (exePath == null) {
                if (s_current == null || (includeUserConfig && !s_currentIncludesUserConfig)) {
                    s_current = new ClientConfigPaths(null, includeUserConfig);
                    s_currentIncludesUserConfig = includeUserConfig;
                }

                result = s_current;
            }
            else {
                result = new ClientConfigPaths(exePath, includeUserConfig);
            }

            return result;
        }

        internal static void RefreshCurrent() {
            s_currentIncludesUserConfig = false;
            s_current = null;
        }

        internal static ClientConfigPaths Current {
            get {
                return GetPaths(null, true);
            }
        }

        internal bool HasEntryAssembly {
            get {
                return _hasEntryAssembly;
            }
        }

        internal string ApplicationUri {
            get {
                return _applicationUri;
            }
        }

        internal string ApplicationConfigUri {
            get {
                return _applicationConfigUri;
            }
        }

        internal string RoamingConfigFilename {
            get {
                return _roamingConfigFilename;
            }
        }

        internal string RoamingConfigDirectory {
            get {
                return _roamingConfigDirectory;
            }
        }

        internal bool HasRoamingConfig {
            get {
                // Assume we have roaming config if we haven't loaded user config file information.
                return RoamingConfigFilename != null || !_includesUserConfig;
            }
        }

        internal string LocalConfigFilename {
            get {
                return _localConfigFilename;
            }
        }

        internal string LocalConfigDirectory {
            get {
                return _localConfigDirectory;
            }
        }

        internal bool HasLocalConfig {
            get {
                // Assume we have roaming config if we haven't loaded user config file information.
                return LocalConfigFilename != null || !_includesUserConfig;
            }
        }

        internal string ProductName {
            get {
                return _productName;
            }
        }

        internal string ProductVersion {
            get {
                return _productVersion;
            }
        }

        private static SecurityPermission ControlEvidencePermission {
            get { 
                if (s_controlEvidencePerm == null) {
                    s_controlEvidencePerm = new SecurityPermission(SecurityPermissionFlag.ControlEvidence);
                }
                return s_controlEvidencePerm;
            } 
        }

        private static SecurityPermission SerializationFormatterPermission {
            get { 
                if (s_serializationPerm == null) {
                    s_serializationPerm = new SecurityPermission(SecurityPermissionFlag.SerializationFormatter);
                }
                return s_serializationPerm;
            } 
        }

        // Combines path2 with path1 if possible, else returns null.
        private string CombineIfValid(string path1, string path2) {
            string returnPath = null;

            if (path1 != null && path2 != null) {
                try {
                    string combinedPath = Path.Combine(path1, path2);
                    if (combinedPath.Length < MAX_PATH) {
                        returnPath = combinedPath;
                    }
                }
                catch {
                }
            }
            
            return returnPath;
        }


        // Mostly borrowed from IsolatedStorage, with some modifications
        private static object GetEvidenceInfo(AppDomain appDomain, string exePath, out string typeName) {
            ControlEvidencePermission.Assert();
            Evidence evidence = appDomain.Evidence;
            StrongName  sn   = null;
            Url         url  = null;

            if (evidence != null) {
                IEnumerator e = evidence.GetHostEnumerator();
                object      temp = null;
    
                while (e.MoveNext()) {
                    temp = e.Current;
    
                    if (temp is StrongName) {
                        sn = (StrongName) temp;
                        break;
                    }
                    else if (temp is Url) {
                        url = (Url) temp;
                    }
                }
            }

            object o = null;

            // The order of preference is StrongName, Url, ExePath.
            if (sn != null) {
                o = sn;
                typeName = StrongNameDesc;
            }
            else if (url != null) {
                // Extract the url string and normalize it to use as evidence
                o = url.Value.ToUpperInvariant();
                typeName = UrlDesc;
            }
            else if (exePath != null) {
                o = exePath;
                typeName = PathDesc;
            }
            else {
                typeName = null;
            }

            return o;
        }


        private bool IsClickOnceDeployed(AppDomain appDomain) {

            return false;
        }

        private void SetNamesAndVersion(string applicationFilename, Assembly exeAssembly, bool isHttp) {
            Type        mainType = null;

            //
            // Get CompanyName, ProductName, and ProductVersion
            // First try custom attributes on the assembly.
            //
            if (exeAssembly != null) {
                object[] attrs = exeAssembly.GetCustomAttributes(typeof(AssemblyCompanyAttribute), false);
                if (attrs != null && attrs.Length > 0) {
                    _companyName = ((AssemblyCompanyAttribute)attrs[0]).Company;
                    if (_companyName != null) {
                        _companyName = _companyName.Trim();
                    }
                }

                attrs = exeAssembly.GetCustomAttributes(typeof(AssemblyProductAttribute), false);
                if (attrs != null && attrs.Length > 0) {
                    _productName = ((AssemblyProductAttribute)attrs[0]).Product;
                    if (_productName != null) {
                        _productName = _productName.Trim();
                    }
                }

                _productVersion = exeAssembly.GetName().Version.ToString();
                if (_productVersion != null) {
                    _productVersion = _productVersion.Trim();
                }
            }

            //
            // If we couldn't get custom attributes, try the Win32 file version
            // 
            if (!isHttp && (String.IsNullOrEmpty(_companyName) || String.IsNullOrEmpty(_productName) || String.IsNullOrEmpty(_productVersion))) {
                string versionInfoFileName = null;

                if (exeAssembly != null) {
                    MethodInfo entryPoint = exeAssembly.EntryPoint;
                    if (entryPoint != null) {
                        mainType = entryPoint.ReflectedType;
                        if (mainType != null) {
                            versionInfoFileName = mainType.Module.FullyQualifiedName;
                        }
                    }
                }

                if (versionInfoFileName == null) {
                    versionInfoFileName = applicationFilename;
                }

            }

            if (String.IsNullOrEmpty(_companyName) || String.IsNullOrEmpty(_productName)) {
                string  ns = null;
                if (mainType != null) {
                    ns = mainType.Namespace;
                }

                // Desperate measures for product name
                if (String.IsNullOrEmpty(_productName)) {
                    // Try the remainder of the namespace
                    if (ns != null) {
                        int lastDot = ns.LastIndexOf(".", StringComparison.Ordinal);
                        if (lastDot != -1 && lastDot < ns.Length - 1) {
                            _productName = ns.Substring(lastDot+1);
                        }
                        else {
                            _productName = ns;
                        }

                        _productName = _productName.Trim();
                    }

                    // Try the type of the entry assembly
                    if (String.IsNullOrEmpty(_productName) && mainType != null) {
                        _productName = mainType.Name.Trim();
                    }

                    // give up, return empty string
                    if (_productName == null) {
                        _productName = string.Empty;
                    }
                }

                // Desperate measures for company name
                if (String.IsNullOrEmpty(_companyName)) {
                    // Try the first part of the namespace
                    if (ns != null) {
                        int firstDot = ns.IndexOf(".", StringComparison.Ordinal);
                        if (firstDot != -1) {
                            _companyName = ns.Substring(0, firstDot);
                        }
                        else {
                            _companyName = ns;
                        }

                        _companyName = _companyName.Trim();
                    }

                    if (String.IsNullOrEmpty(_companyName)) {
                        _companyName = _productName;
                    }
                }
            }

            // Desperate measures for product version - assume 1.0
            if (String.IsNullOrEmpty(_productVersion)) {
                _productVersion = "1.0.0.0";
            }
        }

        // Borrowed from IsolatedStorage
        private static string ToBase32StringSuitableForDirName(byte[] buff) {
            StringBuilder sb = new StringBuilder();
            byte b0, b1, b2, b3, b4;
            int  l, i;
        
            l = buff.Length;
            i = 0;
        
            // Create l chars using the last 5 bits of each byte.  
            // Consume 3 MSB bits 5 bytes at a time.
        
            do
            {
                b0 = (i < l) ? buff[i++] : (byte)0;
                b1 = (i < l) ? buff[i++] : (byte)0;
                b2 = (i < l) ? buff[i++] : (byte)0;
                b3 = (i < l) ? buff[i++] : (byte)0;
                b4 = (i < l) ? buff[i++] : (byte)0;
        
                // Consume the 5 Least significant bits of each byte
                sb.Append(s_Base32Char[b0 & 0x1F]);
                sb.Append(s_Base32Char[b1 & 0x1F]);
                sb.Append(s_Base32Char[b2 & 0x1F]);
                sb.Append(s_Base32Char[b3 & 0x1F]);
                sb.Append(s_Base32Char[b4 & 0x1F]);
        
                // Consume 3 MSB of b0, b1, MSB bits 6, 7 of b3, b4
                sb.Append(s_Base32Char[(
                        ((b0 & 0xE0) >> 5) | 
                        ((b3 & 0x60) >> 2))]);
        
                sb.Append(s_Base32Char[(
                        ((b1 & 0xE0) >> 5) | 
                        ((b4 & 0x60) >> 2))]);
        
                // Consume 3 MSB bits of b2, 1 MSB bit of b3, b4
                
                b2 >>= 5;
        
                if ((b3 & 0x80) != 0)
                    b2 |= 0x08;
                if ((b4 & 0x80) != 0)
                    b2 |= 0x10;
        
                sb.Append(s_Base32Char[b2]);
        
            } while (i < l);
        
            return sb.ToString();
        }

        // Makes the passed in string suitable to use as a path name by replacing illegal characters
        // with underscores. Additionally, we do two things - replace spaces too with underscores and
        // limit the resultant string's length to MAX_LENGTH_TO_USE if limitSize is true.
        private string Validate(string str, bool limitSize) {
            string validated = str;

            if (!String.IsNullOrEmpty(validated)) {
                // First replace all illegal characters with underscores
                foreach (char c in Path.GetInvalidFileNameChars()) {
                    validated = validated.Replace(c, '_');
                }
    
                // Replace all spaces with underscores
                validated = validated.Replace(' ', '_');

                if (limitSize) {
                    validated = (validated.Length > MAX_LENGTH_TO_USE) ? validated.Substring(0, MAX_LENGTH_TO_USE) : validated;
                }
            }

            return validated;
        }
    }
}
