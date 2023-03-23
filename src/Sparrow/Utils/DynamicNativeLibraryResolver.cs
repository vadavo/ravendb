﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sparrow.Utils
{
#if NETCOREAPP3_1_OR_GREATER
    internal static class DynamicNativeLibraryResolver
    {
        private static Dictionary<string, Func<string, string>> _registered = new Dictionary<string, Func<string, string>>();
        private static readonly HashSet<Assembly> _registeredAssemblies = new HashSet<Assembly>();
        public static void Register(Assembly asm, string lib, Func<string, string> mutator = null)
        {
            lock (typeof(DynamicNativeLibraryResolver))
            {
                var copy = new Dictionary<string, Func<string, string>>(_registered)
                {
                    [lib] = mutator
                };
                _registered = copy;
                if (_registeredAssemblies.Add(asm) == false)
                    return;
                NativeLibrary.SetDllImportResolver(asm, Resolver);
            }
        }

        private static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? dllImportSearchPath)
        {
            if (_registered.TryGetValue(libraryName, out var mutator) == false)
                return IntPtr.Zero;

            string suffix;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.ProcessArchitecture != Architecture.Arm &&
                    RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                {
                    suffix = Environment.Is64BitProcess ? ".linux.x64.so" : ".linux.x86.so";
                }
                else
                {
                    suffix = Environment.Is64BitProcess ? ".arm.64.so" : ".arm.32.so";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (RuntimeInformation.ProcessArchitecture != Architecture.Arm &&
                    RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                {
                    suffix = Environment.Is64BitProcess ? ".mac.x64.dylib" : ".mac.x86.dylib";
                }
                else
                {
                    suffix = Environment.Is64BitProcess ? ".mac.arm64.dylib" : "mac.arm32.dylib";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                suffix = Environment.Is64BitProcess ? ".win.x64.dll" : ".win.x86.dll";
            }
            else
            {
                throw new NotSupportedException("Non supported platform - no Linux/OSX/Windows is detected ");
            }

            var name = libraryName + suffix;
            if (mutator != null)
                name = mutator(name);
            return NativeLibrary.Load(name, assembly, dllImportSearchPath);
        }
    }
#endif
}
