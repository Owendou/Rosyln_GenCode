using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeGen
{
    public class ReferenceCheckerException : Exception
    {
        public ReferenceCheckerException(string msg) : base(msg)
        {}
    }

    public class ReferenceChecker : IDisposable
    {
        public ReferenceChecker(CodeGenConfig config)
        {
            mCodeGenConfig = config;

            mWorkspace = MSBuildWorkspace.Create();
            mWorkspace.LoadMetadataForReferencedProjects = true;
            mWorkspace.SkipUnrecognizedProjects = true;

            mProject = mWorkspace.OpenProjectAsync(mCodeGenConfig.Project).Result;

            if (mProject == null)
            {
                throw new ReferenceCheckerException("打开工程文件出错");
            }

            mCompilation = mProject.GetCompilationAsync().Result as CSharpCompilation;

            if (mCompilation == null)
            {
                throw new ReferenceCheckerException("GetCompilationAsync failed");
            }

            mReferenceStrArray = File.ReadAllLines(mCodeGenConfig.Cs2LuaReferenceFilePath);

            for (int i = 0; i < mReferenceStrArray.Length; i++)
            {
                if (mReferenceStrArray[i].StartsWith("CS."))
                {
                    mReferenceStrArray[i] = mReferenceStrArray[i].Replace("CS.", string.Empty);
                }
            }

            mLuaCallCSharpSymbol = mCompilation.GetTypeByMetadataName("XLua.LuaCallCSharpAttribute");

            if (mLuaCallCSharpSymbol == null)
            {
                throw new ReferenceCheckerException("无法获取LuaCallCSharp符号，请确认工程已经导入xlua");
            }
        }

        public void PerformCheck()
        {
            if (mReferenceStrArray.Length == 0)
            {
                return;
            }
            
            MySymbolVisitor symvisitor = new MySymbolVisitor(this);
            symvisitor.Visit(mCompilation.GlobalNamespace);

            symvisitor.LuaCallCSharpSymbols = symvisitor.LuaCallCSharpSymbols.Distinct().ToList();

            Console.ForegroundColor = ConsoleColor.Red;

            foreach (var str in mReferenceStrArray)
            {
                var symbol = mCompilation.GetTypeByMetadataName(str);

                if (symbol != null)
                {
                    if (!symvisitor.LuaCallCSharpSymbols.Contains(symbol))
                    {
                        Console.WriteLine("{0}需要添加[LuaCallCSharp]", str);
                    }
                }
            }

            Console.ResetColor();
            Console.WriteLine("检查完毕");
        }

        public void Dispose()
        {
            if (mWorkspace != null)
            {
                mWorkspace.Dispose();
            }
        }

        private class MySymbolVisitor : SymbolVisitor
        {
            public List<ISymbol> LuaCallCSharpSymbols = new List<ISymbol>();

            private ReferenceChecker mChecker;

            public MySymbolVisitor(ReferenceChecker check)
            {
                mChecker = check;
            }

            public override void VisitNamespace(INamespaceSymbol symbol)
            {
                foreach (var child in symbol.GetMembers())
                {
                    child.Accept(this);
                }
            }

            public override void VisitField(IFieldSymbol symbol)
            {
                foreach (var attr in symbol.GetAttributes())
                {
                    if (attr.AttributeClass == mChecker.mLuaCallCSharpSymbol)
                    {
                        VariableDeclaratorSyntax node = symbol.DeclaringSyntaxReferences.Single().GetSyntax() as VariableDeclaratorSyntax;

                        if (node != null)
                        {
                            var allTypes = node.DescendantNodes().OfType<TypeOfExpressionSyntax>().Select((a) => a.Type);

                            var semanticMode = mChecker.mCompilation.GetSemanticModel(node.SyntaxTree);

                            foreach (var t in allTypes)
                            {
                                var symbolInfo = semanticMode.GetSymbolInfo(t);

                                if (symbolInfo.Symbol != null)
                                {
                                    LuaCallCSharpSymbols.Add(symbolInfo.Symbol);
                                }
                                else
                                {
                                    Console.WriteLine("failed to get GetSymbolInfo from " + t.ToString());
                                }
                            }
                        }
                    }
                }
            }

            public override void VisitNamedType(INamedTypeSymbol symbol)
            {
                foreach (var attr in symbol.GetAttributes())
                {
                    if (attr.AttributeClass == mChecker.mLuaCallCSharpSymbol)
                    {
                        LuaCallCSharpSymbols.Add(symbol);
                    }
                }

                foreach (var child in symbol.GetMembers())
                {
                    child.Accept(this);
                }
            }
        }

        private CodeGenConfig mCodeGenConfig;
        private MSBuildWorkspace mWorkspace;
        private Project mProject;
        private CSharpCompilation mCompilation;
        private string[] mReferenceStrArray;
        private INamedTypeSymbol mLuaCallCSharpSymbol;
    }
}