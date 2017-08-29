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

using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace AspectCompiler
{
    class Program
    {
        static string basePath;
        static void Main(string[] args)
        {
            var asmFile = args[0];
            var fileInfo = new FileInfo(asmFile);

            if (!fileInfo.Exists)
            {
                asmFile = Path.Combine(fileInfo.Directory.FullName, Path.GetFileNameWithoutExtension(fileInfo.Name), fileInfo.Name);
                Console.WriteLine("PostAspect - Try alternative path: {0}", asmFile);
            }

            try
            {
                ProcessAssembly(asmFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw;
            }
        }

        private static void ProcessAssembly(string asmFile)
        {
            var sw = new Stopwatch();
            var asmLoadSw = new Stopwatch();
            var saveSw = new Stopwatch();
            var processSw = new Stopwatch();

            basePath = new FileInfo(asmFile).Directory.FullName;

            Environment.CurrentDirectory = basePath;

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var asmName = new AssemblyName(e.Name);
                var asmPath = Path.Combine(Environment.CurrentDirectory, asmName.Name + ".dll");

                using (var fs = new FileStream(asmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        return Assembly.Load(ms.ToArray());
                    }
                }
            };

            var fileName = asmFile;
            var fileInfo = new FileInfo(fileName);
            var directory = fileInfo.Directory;
            var targetFolder = fileName.Contains("/obj/") ? "obj" : "bin";

            while (!directory.Name.Equals(targetFolder, StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.Parent;
            }

            directory = directory.Parent;
            var noAspectFile = Path.Combine(directory.FullName, "no.aspects");
            
            if (File.Exists(noAspectFile) || string.Equals(Environment.GetEnvironmentVariable("DISABLE_POSTASPECT"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Console.WriteLine("Started post-aspect Post-Processing for {0}", asmFile);

            var pdbFile = new FileInfo(Path.Combine(basePath, Path.GetFileNameWithoutExtension(asmFile) + ".pdb"));
            var useSymbols = pdbFile.Exists || asmFile.IndexOf("/debug/", StringComparison.OrdinalIgnoreCase) >= 0;

            sw.Start();

            asmLoadSw.Start();
            var definition = ModuleDefinition.ReadModule(asmFile, new ReaderParameters { ReadSymbols = useSymbols });

            asmLoadSw.Stop();

            Console.WriteLine("post-aspect Assembly Load in {0} ms", asmLoadSw.ElapsedMilliseconds);

            var moduleWeaver = new ModuleWeaver
            {
                ModuleDefinition = definition
            };

            processSw.Start();
            moduleWeaver.Execute();
            processSw.Stop();

            Console.WriteLine("post-aspect Assembly Rewrite in {0} ms", processSw.ElapsedMilliseconds);

            saveSw.Start();
            //Write to disk
            definition.Write(asmFile, new WriterParameters { WriteSymbols = useSymbols });
            saveSw.Stop();

            Console.WriteLine("Saved post-aspect Processed Assembly in {0} ms", saveSw.ElapsedMilliseconds);

            sw.Stop();
            Console.WriteLine("Completed post-aspect Post-Processing in {0} ms", sw.ElapsedMilliseconds);

            Environment.ExitCode = 0;
        }
    }
}
