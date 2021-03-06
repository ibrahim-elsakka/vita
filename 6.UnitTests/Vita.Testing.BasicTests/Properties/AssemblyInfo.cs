using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Vita.Testing.BasicTests")]
[assembly: AssemblyDescription("VITA basic unit tests")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCompany("Roman Ivantsov")]
[assembly: AssemblyProduct("Vita.Testing.BasicTests")]
[assembly: AssemblyCopyright("Copyright © Roman Ivantsov 2018")]
[assembly: AssemblyTrademark("VITA")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// There are some non CLS compliant types in DataTypesEntity (ex: sbyte). 
// We cannot make it internal - proxy emitter needs interfaces to be public
[assembly: CLSCompliant(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("075d7e20-9859-46b4-8b0d-10a78d32bca5")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("3.0.3")]

[assembly: AssemblyVersion("3.0.3")]
[assembly: AssemblyFileVersion("3.0.3")]
