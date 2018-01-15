using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Simplification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeGen.Source
{
    public class CodeGenException : Exception
    {
        public CodeGenException(string msg) : base(msg)
        {

        }
    }


    class SimpleClass
    {
        public void SimpleMethod()
        {

        }
    }

    public class TestClassGenContext
    {
        public string TargetName;
        public string TargetDir;

        public INamedTypeSymbol TypeSymbol;
        public SyntaxGenerator SyntaxGenerator;

        public List<IMethodSymbol> MethodSymbols = new List<IMethodSymbol>();
        public List<IMethodSymbol> NormalMethodSymbols = new List<IMethodSymbol>();
        public List<IPropertySymbol> PropertySymbols = new List<IPropertySymbol>();

    }

    class CodeGenerator : IDisposable
    {
        //环境配置
        private CodeGenConfig2 mCodeGenConfig;
        private MSBuildWorkspace mWorkspace;
        private Project m_Project;
        private CSharpCompilation mCompilation;

        private ITypeSymbol mFunctionTypeSymbol;
        private ITypeSymbol mSystemExceptionTypeSymbol;

        //解析
        private IResolveReference mReferenceResolver;

        public void Dispose()
        {
            if(mWorkspace != null)
            {
                mWorkspace.Dispose();
            }
        }

        public CodeGenerator(CodeGenConfig2 config)
        {
            mCodeGenConfig = config;

            if (!File.Exists(mCodeGenConfig.Project))
            {
                throw new CodeGenException("Solution文件不存在");
            }

            mWorkspace = MSBuildWorkspace.Create(new Dictionary<string, string> { { "CheckForSystemRuntimeDependency", "true" } });

            m_Project = mWorkspace.OpenProjectAsync(mCodeGenConfig.Project).Result;

            if (m_Project == null)
            {
                throw new CodeGenException("打开工程文件出错");
            }
            else
            {
                Console.WriteLine("工程名:{0}", m_Project.Name);
            }

            mReferenceResolver = new ResolveReferenceByManual();

            mCompilation = mReferenceResolver.GetReferenceResolvedCompilation(config);

            if (mCompilation == null)
            {
                throw new CodeGenException("GetCompilationAsync failed");
            }

            if (mCompilation.GetDiagnostics().Where((o) => o.Severity == DiagnosticSeverity.Error).Any())
            {
                //var diag = mCompilation./
            }
        }

        public bool InitTestClassGenContext(TestClassGenContext context, GenProxyClass item)
        {
            context.TargetName = item.TargetName;
            context.TargetDir = item.TargetDir;

            context.SyntaxGenerator = SyntaxGenerator.GetGenerator(m_Project);
            return true;
        }

        public void PreformProxyClassGeneration(TestClassGenContext context)
        {
            var typeSymbol = context.TypeSymbol;
            var syntaxGen = context.SyntaxGenerator;

            // Generate using
            var usingDirectives = new SyntaxNode[] { syntaxGen.NamespaceImportDeclaration("System")};

            // Generate normal method
            var normalMethodNodes = new List<SyntaxNode>();

            for(int i = 0; i < 1; ++i)
            {
                normalMethodNodes.Add(GenFunctionBoby(context, null, typeSymbol, syntaxGen));
            }

            var classDefin = syntaxGen.ClassDeclaration(context.TargetName,
                                                            typeParameters: null,
                                                            accessibility: Accessibility.Public,
                                                            modifiers: DeclarationModifiers.None,
                                                            baseType: null,
                                                            interfaceTypes: null,
                                                            members: normalMethodNodes);

            usingDirectives[0] = AddComment(usingDirectives[0]);

            SyntaxNode namespaceDeclaration = null;

            var finalNode = syntaxGen.CompilationUnit(usingDirectives.Union(new SyntaxNode[] { namespaceDeclaration ?? classDefin })).NormalizeWhitespace();

            var path = Path.Combine(context.TargetDir, context.TargetName);
            path = Path.ChangeExtension(path, ".cs");

            string str = SimplifyGenCode(context.TargetName, finalNode);

            using (var sw = File.CreateText(path))
            {

                sw.Write(str);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("代码生成完毕");
                Console.ReadKey();
            }
        }

        private string SimplifyGenCode(string docName, SyntaxNode node)
        {
            var document = m_Project.AddDocument(docName, node.GetText());
            var annotatedDocu = document.WithSyntaxRoot(document.GetSyntaxRootAsync().Result.WithAdditionalAnnotations(Simplifier.Annotation));

            var reduceDocument = Simplifier.ReduceAsync(annotatedDocu).Result;

            var reducedDocumentText = reduceDocument.GetTextAsync().Result;
            return reducedDocumentText.ToString();
        }

        public SyntaxNode GenFunctionBoby(TestClassGenContext context, IMethodSymbol methodSymbol, INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen)
        {

            // void foo() {};
             
            var variableIdentifier = syntaxGen.IdentifierName("m_Val");

            List<SyntaxNode> statements = new List<SyntaxNode>();

            statements.Add(syntaxGen.ReturnStatement(syntaxGen.IdentifierName("0")));

            return syntaxGen.MethodDeclaration("Foo", null, null, null, Accessibility.Public, DeclarationModifiers.None, statements); ;
        }

        private SyntaxNode AddComment(SyntaxNode node)
        {
            if (mCodeGenConfig.DontGenerateComment)
            {
                return node;
            }

            var usingNode = node as UsingDirectiveSyntax;

            if (usingNode != null)
            {
                usingNode = usingNode.WithUsingKeyword
                (
                    SyntaxFactory.Token
                    (
                        SyntaxFactory.TriviaList
                        (
                            new[]
                            {
                                SyntaxFactory.Comment("//"),
                                SyntaxFactory.Comment(string.Format("// This is generated by CodeGen at {0} {1}", DateTime.Now.ToShortDateString(), DateTime.Now.ToLongTimeString())),
                                SyntaxFactory.Comment("//")
                            }
                        ),
                        SyntaxKind.UsingKeyword,
                        SyntaxFactory.TriviaList()
                    )
                );

                return usingNode;
            }
            else
            {
                return node;
            }
        }

        public void GenCode()
        {
            foreach (var item in mCodeGenConfig.ProxyClassItems)
            {
                Console.WriteLine("生成{0}类中", item.TargetName);

                TestClassGenContext context = new TestClassGenContext();

                if (InitTestClassGenContext(context, item))
                {
                    PreformProxyClassGeneration(context);
                }
            }
        }
    }
}
