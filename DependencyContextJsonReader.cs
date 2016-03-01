﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Extensions.DependencyModel
{
    public class DependencyContextJsonReader
    {
        public DependencyContext Read(Stream stream)
        {
            using (var streamReader = new StreamReader(stream))
            {
                using (var reader = new JsonTextReader(streamReader))
                {
                    var root = JObject.Load(reader);
                    return Read(root);
                }
            }
        }

        private bool IsRuntimeTarget(string name) => name.Contains(DependencyContextStrings.VersionSeperator);

        private DependencyContext Read(JObject root)
        {
            string runtime = null;
            string target = null;

            var runtimeTargetInfo = ReadRuntimeTargetInfo(root);
            var libraryStubs = ReadLibraryStubs((JObject) root[DependencyContextStrings.LibrariesPropertyName]);
            var targetsObject = (IEnumerable<KeyValuePair<string, JToken>>) root[DependencyContextStrings.TargetsPropertyName];

            JObject runtimeTarget = null;
            JObject compileTarget = null;
            if (targetsObject != null)
            {
                var compileTargetProperty = targetsObject.FirstOrDefault(t => !IsRuntimeTarget(t.Key));
                compileTarget = (JObject) compileTargetProperty.Value;
                target = compileTargetProperty.Key;

                if (!string.IsNullOrEmpty(runtimeTargetInfo.Name))
                {
                    runtimeTarget = (JObject) targetsObject.FirstOrDefault(t => t.Key == runtimeTargetInfo.Name).Value;
                    if (runtimeTarget == null)
                    {
                        throw new FormatException($"Target with name {runtimeTargetInfo.Name} not found");
                    }
                    runtime = runtimeTargetInfo.Name.Substring(target.Length + 1);
                }
                else
                {
                    runtimeTarget = compileTarget;
                }

            }
            
            return new DependencyContext(
                target,
                runtime,
                runtimeTargetInfo.Portable,
                ReadCompilationOptions((JObject)root[DependencyContextStrings.CompilationOptionsPropertName]),
                ReadLibraries(compileTarget, false, libraryStubs).Cast<CompilationLibrary>().ToArray(),
                ReadLibraries(runtimeTarget, true, libraryStubs).Cast<RuntimeLibrary>().ToArray(),
                ReadRuntimeGraph((JObject)root[DependencyContextStrings.RuntimesPropertyName]).ToArray()
                );
        }

        private IEnumerable<KeyValuePair<string, string[]>> ReadRuntimeGraph(JObject runtimes)
        {
            if (runtimes == null)
            {
                yield break;
            }

            var targets = runtimes.Children();
            var runtime = (JProperty)targets.Single();
            foreach (var pair in (JObject)runtime.Value)
            {
                yield return new KeyValuePair<string, string[]>(pair.Key, pair.Value.Values<string>().ToArray());
            }
        }

        private RuntimeTargetInfo ReadRuntimeTargetInfo(JObject root)
        {

            var runtimeTarget = (JObject)root[DependencyContextStrings.RuntimeTargetPropertyName];
            if (runtimeTarget != null)
            {
                return new RuntimeTargetInfo()
                {
                    Name = runtimeTarget[DependencyContextStrings.RuntimeTargetNamePropertyName]?.Value<string>(),
                    Portable = runtimeTarget[DependencyContextStrings.PortablePropertyName]?.Value<bool>() == true
                };
            }
            return new RuntimeTargetInfo()
            {
                Portable = true
            };
        }

        private CompilationOptions ReadCompilationOptions(JObject compilationOptionsObject)
        {
            if (compilationOptionsObject == null)
            {
                return CompilationOptions.Default;
            }

            return new CompilationOptions(
                compilationOptionsObject[DependencyContextStrings.DefinesPropertyName]?.Values<string>(),
                compilationOptionsObject[DependencyContextStrings.LanguageVersionPropertyName]?.Value<string>(),
                compilationOptionsObject[DependencyContextStrings.PlatformPropertyName]?.Value<string>(),
                compilationOptionsObject[DependencyContextStrings.AllowUnsafePropertyName]?.Value<bool>(),
                compilationOptionsObject[DependencyContextStrings.WarningsAsErrorsPropertyName]?.Value<bool>(),
                compilationOptionsObject[DependencyContextStrings.OptimizePropertyName]?.Value<bool>(),
                compilationOptionsObject[DependencyContextStrings.KeyFilePropertyName]?.Value<string>(),
                compilationOptionsObject[DependencyContextStrings.DelaySignPropertyName]?.Value<bool>(),
                compilationOptionsObject[DependencyContextStrings.PublicSignPropertyName]?.Value<bool>(),
                compilationOptionsObject[DependencyContextStrings.DebugTypePropertyName]?.Value<string>(),
                compilationOptionsObject[DependencyContextStrings.EmitEntryPointPropertyName]?.Value<bool>(),
                compilationOptionsObject[DependencyContextStrings.GenerateXmlDocumentationPropertyName]?.Value<bool>()
                );
        }

        private IEnumerable<Library> ReadLibraries(JObject librariesObject, bool runtime, Dictionary<string, LibraryStub> libraryStubs)
        {
            if (librariesObject == null)
            {
                return Enumerable.Empty<Library>();
            }
            return librariesObject.Properties().Select(property => ReadLibrary(property, runtime, libraryStubs));
        }

        private Library ReadLibrary(JProperty property, bool runtime, Dictionary<string, LibraryStub> libraryStubs)
        {
            var nameWithVersion = property.Name;
            LibraryStub stub;

            if (!libraryStubs.TryGetValue(nameWithVersion, out stub))
            {
                throw new InvalidOperationException($"Cannot find library information for {nameWithVersion}");
            }

            var seperatorPosition = nameWithVersion.IndexOf(DependencyContextStrings.VersionSeperator);

            var name = nameWithVersion.Substring(0, seperatorPosition);
            var version = nameWithVersion.Substring(seperatorPosition + 1);

            var libraryObject = (JObject) property.Value;

            var dependencies = ReadDependencies(libraryObject);
            var assemblies = ReadAssemblies(libraryObject, runtime);

            if (runtime)
            {
                return new RuntimeLibrary(stub.Type, name, version, stub.Hash, assemblies, new RuntimeTarget[0], dependencies, stub.Serviceable);
            }
            else
            {
                return new CompilationLibrary(stub.Type, name, version, stub.Hash, assemblies, dependencies, stub.Serviceable);
            }
        }

        private static string[] ReadAssemblies(JObject libraryObject, bool runtime)
        {
            var assembliesObject = (JObject) libraryObject[runtime ? DependencyContextStrings.RuntimeAssembliesKey : DependencyContextStrings.CompileTimeAssembliesKey];

            if (assembliesObject == null)
            {
                return new string[] {};
            }

            return assembliesObject.Properties().Select(property => property.Name).ToArray();
        }

        private static Dependency[] ReadDependencies(JObject libraryObject)
        {
            var dependenciesObject = (JObject) libraryObject[DependencyContextStrings.DependenciesPropertyName];

            if (dependenciesObject == null)
            {
                return new Dependency[]{ };
            }

            return dependenciesObject.Properties()
                .Select(property => new Dependency(property.Name, (string) property.Value)).ToArray();
        }

        private Dictionary<string, LibraryStub> ReadLibraryStubs(JObject librariesObject)
        {
            var libraries = new Dictionary<string, LibraryStub>();
            if (librariesObject != null)
            {
                foreach (var libraryProperty in librariesObject)
                {
                    var value = (JObject) libraryProperty.Value;
                    var stub = new LibraryStub
                    {
                        Name = libraryProperty.Key,
                        Hash = value[DependencyContextStrings.Sha512PropertyName]?.Value<string>(),
                        Type = value[DependencyContextStrings.TypePropertyName].Value<string>(),
                        Serviceable = value[DependencyContextStrings.ServiceablePropertyName]?.Value<bool>() == true
                    };
                    libraries.Add(stub.Name, stub);
                }
            }
            return libraries;
        }

        private struct RuntimeTargetInfo
        {
            public string Name;

            public bool Portable;
        }

        private struct LibraryStub
        {
            public string Name;

            public string Hash;

            public string Type;

            public bool Serviceable;
        }
    }
}
