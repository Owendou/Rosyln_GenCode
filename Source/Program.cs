using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.MSBuild;
using System.Xml.Serialization;
using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using CodeGen.Source;

namespace CodeGen
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 1)
            {
                var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { { "CheckForSystemRuntimeDependency", "true" } });

                workspace.LoadMetadataForReferencedProjects = true;
                workspace.SkipUnrecognizedProjects = true;

                var solution = workspace.OpenSolutionAsync(args[0]).Result;

                foreach (var project in solution.Projects)
                {
                    var compilation = project.GetCompilationAsync().Result as CSharpCompilation;


                    if (compilation.GetTypeByMetadataName("System.Exception") != null)
                    {
                        Console.WriteLine("Get System.Exception success " + project.Name);
                    }
                    else
                    {
                        Console.WriteLine("Get System.Exception failed " + project.Name);
                    }
                }

                Console.ReadKey();

                return;
            }

            try
            {
                CodeGenConfig2 config = CodeGenConfig2.Load("CodeGenConfig2.xml");

                if (config.ProxyClassItems.Count != 0)
                {
                    Console.WriteLine("正在生成代码");

                    using (CodeGenerator codeGen = new CodeGenerator(config))
                    {
                        codeGen.GenCode();
                    }
                }

                //if (File.Exists(config.Cs2LuaReferenceFilePath))
                //{
                //    Console.WriteLine("正在检查外部引用是否都加上[LuaCallCSharp]");

                //    using (ReferenceChecker refChecker = new ReferenceChecker(config))
                //    {
                //        refChecker.PerformCheck();
                //    }
                //}
            }
            catch (CodeGenException ex)
            {
                Console.WriteLine(ex.Message);
            }

            Console.WriteLine("按任意键结束");
            Console.ReadKey();
        }
    }
}
