using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Xml;
using System.IO;
using System.Reflection;
using CodeGen.Source;

namespace CodeGen
{
    class ResolveReferenceByManual : IResolveReference
    {
        CSharpCompilation IResolveReference.GetReferenceResolvedCompilation(CodeGenConfig2 config)
        {
            string path = Path.GetDirectoryName(config.Project);
            string projFilePath = config.Project;
            string name = Path.GetFileNameWithoutExtension(projFilePath);

            CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            compilationOptions = compilationOptions.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);

            CSharpCompilation compilation = CSharpCompilation.Create(name);
            compilation = compilation.WithOptions(compilationOptions);

            return compilation;
        }
        
        CSharpCompilation IResolveReference.GetReferenceResolvedCompilation(CodeGenConfig config)
        {
            string path = Path.GetDirectoryName(config.Project);
            string projFilePath = config.Project;
            string name = Path.GetFileNameWithoutExtension(projFilePath);

            string systemDllPath = config.SystemDllPath;

            List<string> preprocessors = new List<string>();
            preprocessors.Add("__LUA__");

            //存储引用refs
            List<MetadataReference> refs = new List<MetadataReference>();

            List<string> files = new List<string>();
            Dictionary<string, string> refByNames = new Dictionary<string, string>();
            Dictionary<string, string> refByPaths = new Dictionary<string, string>();

            //从csproj文件中解析XML获取引用
            if (projFilePath.EndsWith(".csproj"))
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(projFilePath);

                //查找ItemGroup下的Reference节点
                var nodes = SelectNodes(xmlDoc, "ItemGroup", "Reference");
                foreach (XmlElement node in nodes)
                {
                    var aliasesNode = SelectSingleNode(node, "Aliases");
                    var pathNode = SelectSingleNode(node, "HintPath");
                    if (null != pathNode && !refByPaths.ContainsKey(pathNode.InnerText))
                    {
                        if (null != aliasesNode)
                            refByPaths.Add(pathNode.InnerText, aliasesNode.InnerText);
                        else
                            refByPaths.Add(pathNode.InnerText, "global");
                    }
                    else
                    {
                        string val = node.GetAttribute("Include");
                        if (!string.IsNullOrEmpty(val) && !refByNames.ContainsKey(val))
                        {
                            if (null != aliasesNode)
                                refByNames.Add(val, aliasesNode.InnerText);
                            else
                                refByNames.Add(val, "global");
                        }
                    }
                }
                
                string prjOutputDir = "bin/Debug/";
                nodes = SelectNodes(xmlDoc, "PropertyGroup");
                foreach (XmlElement node in nodes)
                {
                    string condition = node.GetAttribute("Condition");
                    var defNode = SelectSingleNode(node, "DefineConstants");
                    var pathNode = SelectSingleNode(node, "OutputPath");
                    if (null != defNode && null != pathNode)
                    {
                        string text = defNode.InnerText.Trim();
                        if (condition.IndexOf("Debug") > 0 || condition.IndexOf("Release") < 0 && (text == "DEBUG" || text.IndexOf(";DEBUG;") > 0 || text.StartsWith("DEBUG;") || text.EndsWith(";DEBUG")))
                        {
                            preprocessors.AddRange(text.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                            prjOutputDir = pathNode.InnerText.Trim();
                            break;
                        }
                    }
                }

                //查找ItemGroup下的ProjectReference节点
                nodes = SelectNodes(xmlDoc, "ItemGroup", "ProjectReference");
                foreach (XmlElement node in nodes)
                {
                    string val = node.GetAttribute("Include");
                    string prjFile = Path.Combine(path, val.Trim());
                    var nameNode = SelectSingleNode(node, "Name");
                    if (null != prjFile && null != nameNode)
                    {
                        string prjName = nameNode.InnerText.Trim();
                        string prjOutputFile = ParseProjectOutputFile(prjFile, prjName);
                        string fileName = Path.Combine(prjOutputDir, prjOutputFile);
                        if (!refByPaths.ContainsKey(fileName))
                        {
                            refByPaths.Add(fileName, "global");
                        }
                    }
                }
                nodes = SelectNodes(xmlDoc, "ItemGroup", "Compile");
                foreach (XmlElement node in nodes)
                {
                    string val = node.GetAttribute("Include");
                    if (!string.IsNullOrEmpty(val) && val.EndsWith(".cs") && !files.Contains(val))
                    {
                        files.Add(val);
                    }
                }

                //确保常用的Assembly被引用
                if (!refByNames.ContainsKey("mscorlib"))
                {
                    refByNames.Add("mscorlib", "global");
                }
                if (!refByNames.ContainsKey("System"))
                {
                    refByNames.Add("System", "global");
                }
                if (!refByNames.ContainsKey("System.Core"))
                {
                    refByNames.Add("System.Core", "global");
                }

                if (string.IsNullOrEmpty(systemDllPath))//默认
                {
                    //用反射添加具体的类名引用
                    foreach (var pair in refByNames)
                    {
                       
#pragma warning disable 618
                        Assembly assembly = Assembly.LoadWithPartialName(pair.Key);
#pragma warning restore 618
                        if (null != assembly)
                        {
                            var arr = System.Collections.Immutable.ImmutableArray.Create(pair.Value);
                            Console.WriteLine(pair.Key);
                            refs.Add(MetadataReference.CreateFromFile(assembly.Location, new MetadataReferenceProperties(MetadataImageKind.Assembly, arr)));
                        }
                    }
                }
                //需配置SystemDLLPath
                else
                {
                    foreach (var pair in refByNames)
                    {
                        string file = Path.Combine(systemDllPath, pair.Key) + ".dll";
                        var arr = System.Collections.Immutable.ImmutableArray.Create(pair.Value);
                        refs.Add(MetadataReference.CreateFromFile(file, new MetadataReferenceProperties(MetadataImageKind.Assembly, arr)));
                    }
                }

                //从文件路径添加DLL引用
                foreach (var pair in refByPaths)
                {
                    string fullPath = Path.Combine(path, pair.Key);
                    var arr = System.Collections.Immutable.ImmutableArray.Create(pair.Value);
                    Console.WriteLine(fullPath);


                    if (!File.Exists(fullPath))
                    {
                        Console.WriteLine(fullPath + "is not exsit!");
                        continue;
                    }

                    refs.Add(MetadataReference.CreateFromFile(fullPath, new MetadataReferenceProperties(MetadataImageKind.Assembly, arr)));
                }

                //手动构造Complation对象
                CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
                compilationOptions = compilationOptions.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
                CSharpCompilation compilation = CSharpCompilation.Create(name);
                compilation = compilation.WithOptions(compilationOptions);

                //手动添加Reference
                compilation = compilation.AddReferences(refs.ToArray());

                //手动添加SyntaxTree
                foreach (string itemFile in files)
                {
                    string filePath = Path.Combine(path, itemFile);
                    string fileName = Path.GetFileNameWithoutExtension(filePath);

                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine(filePath + "is not exsit!");
                        continue;
                    }

                    CSharpParseOptions options = new CSharpParseOptions();
                    options = options.WithPreprocessorSymbols(preprocessors);
                    options = options.WithFeatures(new Dictionary<string, string> { { "IOperation", "true" } });
                    SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), options, filePath);
                    compilation = compilation.AddSyntaxTrees(tree);
                }
                return compilation;
            }
            return null;
        }

        private static string ParseProjectOutputFile(string srcFile, string prjName)
        {
            string fileName = prjName + ".dll";
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(srcFile);
            var nodes = SelectNodes(xmlDoc, "PropertyGroup");
            foreach (XmlElement node in nodes)
            {
                var typeNode = SelectSingleNode(node, "OutputType");
                var nameNode = SelectSingleNode(node, "AssemblyName");
                if (null != typeNode && null != nameNode)
                {
                    string type = typeNode.InnerText.Trim();
                    string name = nameNode.InnerText.Trim();
                    fileName = name + (type == "Library" ? ".dll" : ".exe");
                }
            }
            return fileName;
        }

        private static List<XmlElement> SelectNodes(XmlNode node, params string[] names)
        {
            return SelectNodesRecursively(node, 0, names);
        }

        private static List<XmlElement> SelectNodesRecursively(XmlNode node, int index, params string[] names)
        {
            string name = names[index];
            List<XmlElement> list = new List<XmlElement>();
            foreach (var cnode in node.ChildNodes)
            {
                var element = cnode as XmlElement;
                if (null != element)
                {
                    if (element.Name == name)
                    {
                        if (index < names.Length - 1)
                        {
                            list.AddRange(SelectNodesRecursively(element, index + 1, names));
                        }
                        else
                        {
                            list.Add(element);
                        }
                    }
                    else if (index == 0)
                    {
                        list.AddRange(SelectNodesRecursively(element, index, names));
                    }
                }
            }
            return list;
        }

        private static XmlElement SelectSingleNode(XmlNode node, params string[] names)
        {
            return SelectSingleNodeRecursively(node, 0, names);
        }
        private static XmlElement SelectSingleNodeRecursively(XmlNode node, int index, params string[] names)
        {
            XmlElement ret = null;
            string name = names[index];
            foreach (var cnode in node.ChildNodes)
            {
                var element = cnode as XmlElement;
                if (null != element)
                {
                    if (element.Name == name)
                    {
                        if (index < names.Length - 1)
                        {
                            ret = SelectSingleNodeRecursively(element, index + 1, names);
                        }
                        else
                        {
                            ret = element;
                        }
                    }
                    else if (index == 0)
                    {
                        ret = SelectSingleNodeRecursively(element, index, names);
                    }
                    if (null != ret)
                    {
                        break;
                    }
                }
            }
            return ret;
        }
    }
}
