/*
 * Copyright (c) 2017 VMware, Inc. All Rights Reserved.
 * 
 * Licensed under the MIT License, Version 2.0 (the "License"); 
 * You may not use this file except in compliance with the License. 
 * You may obtain a copy of the License at 
 * 
 *     https://opensource.org/licenses/MIT
 *  
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Xunit;
using System.Collections.Generic;
using AssemblyToProcess;

namespace MrAspect.Tests
{
    public class WeaverTests
    {
        static Assembly assembly;
        static string newAssemblyPath;
        static string assemblyPath;

        static WeaverTests()
        {
            var projectPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\..\..\AssemblyToProcess\AssemblyToProcess.csproj"));
            assemblyPath = Path.Combine(Path.GetDirectoryName(projectPath), @"bin\Debug\AssemblyToProcess.dll");
            var pdbPath = Path.Combine(Path.GetDirectoryName(projectPath), @"bin\Debug\AssemblyToProcess.pdb");
#if (!DEBUG)
        assemblyPath = assemblyPath.Replace("Debug", "Release");
#endif

            newAssemblyPath = assemblyPath.Replace(".dll", "2.dll");

            File.Copy(assemblyPath, newAssemblyPath, true);

            var newPDBPath = pdbPath.Replace(".pdb", "2.pdb");

            if (File.Exists(newPDBPath))
            {
                File.Copy(pdbPath, newPDBPath, true);
            }

            var moduleDefinition = ModuleDefinition.ReadModule(newAssemblyPath, new ReaderParameters { ReadSymbols = false });
            var weavingTask = new ModuleWeaver
            {
                ModuleDefinition = moduleDefinition
            };

            weavingTask.Execute();
            moduleDefinition.Write(newAssemblyPath, new WriterParameters { WriteSymbols = false });

            assembly = Assembly.LoadFile(newAssemblyPath);

            //PeVerify();
        }

        [Fact]
        public void TestInjection()
        {
            var type = assembly.GetType("AssemblyToProcess.Sample");
            var instance = (dynamic)Activator.CreateInstance(type);
            instance.TestMethod();
        }

#if (DEBUG)
        [Fact]
        public static void PeVerify()
        {
            Verifier.Verify(assemblyPath, newAssemblyPath);
        }
#endif
    }
}
