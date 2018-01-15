using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace CodeGen
{
    public class CodeGenException : Exception
    {
        public CodeGenException(string msg) : base(msg)
        {}
    }

    public class ProxyClassGenContext
    {
        public string TargetName;
        public string TargetDir;
        public INamedTypeSymbol TypeSymbol;
        public SyntaxGenerator SyntaxGenerator;

        public List<IMethodSymbol> MethodSymbols = new List<IMethodSymbol>();
        public List<IMethodSymbol> NormalMethodSymbols = new List<IMethodSymbol>();
        public List<IPropertySymbol> PropertySymbols = new List<IPropertySymbol>();
        public List<IMethodSymbol> DelegateCallMethodSymbols = new List<IMethodSymbol>();

        public Dictionary<ISymbol, string> NameDic = new Dictionary<ISymbol, string>();
    }

    public class InterfaceGenContext
    {
        public string TargetName;
        public string TargetDir;
        public INamedTypeSymbol TypeSymbol;
        public SyntaxGenerator SyntaxGenerator;

        public List<IMethodSymbol> MethodSymbols = new List<IMethodSymbol>();
        public List<IMethodSymbol> NormalMethodSymbols = new List<IMethodSymbol>();
        public List<IPropertySymbol> PropertySymbols = new List<IPropertySymbol>();
    }

    public class CodeGenImpl : IDisposable
    {
        public CodeGenImpl(CodeGenConfig config)
        {
            mCodeGenConfig = config;

            if (!File.Exists(mCodeGenConfig.Project))
            {
                throw new CodeGenException("Solution文件不存在");
            }

            mWorkspace = MSBuildWorkspace.Create(new Dictionary<string, string> { { "CheckForSystemRuntimeDependency", "true" } });
            mWorkspace.LoadMetadataForReferencedProjects = true;
            mWorkspace.SkipUnrecognizedProjects = true;

            mProject = mWorkspace.OpenProjectAsync(mCodeGenConfig.Project).Result;

            if (mProject == null)
            {
                throw new CodeGenException("打开工程文件出错");
            }
            else
            {
                Console.WriteLine("工程名：{0}", mProject.Name);
            }

            if (config.IsResolveReferenceManually)
            {
                Console.WriteLine("By手动解析引用");
                mReferenceResolver = new ResolveReferenceByManual();
            }
            else
            {
                Console.WriteLine("ByRosyln自动解析");
                mReferenceResolver = new ResolveReferenceByRosyln(mProject);
            }

            mCompilation = mReferenceResolver.GetReferenceResolvedCompilation(config);

            if (mCompilation == null)
            {
                throw new CodeGenException("GetCompilationAsync failed");
            }

            if (mCompilation.GetDiagnostics().Where((a) => a.Severity == DiagnosticSeverity.Error).Any())
            {
                var diagnostis=mCompilation.GetDiagnostics().Where((a) => a.Severity == DiagnosticSeverity.Error).ToArray();

                foreach( var item in diagnostis)
                {
                    Console.WriteLine(item.ToString());
                }

                throw new CodeGenException("存在编译错误");
            }

            mLuaTableTypeSymbol = mCompilation.GetTypeByMetadataName("XLua.LuaTable");

            if (mLuaTableTypeSymbol == null)
            {
                throw new CodeGenException("mLuaTableTypeSymbol empty");
            }

            mSystemExceptionTypeSymbol = mCompilation.GetTypeByMetadataName("System.Exception");

            if (mSystemExceptionTypeSymbol == null)
            {
                throw new CodeGenException("mSystemExceptionTypeSymbol empty");
            }

            mLuaFunctionTypeSymbol = mCompilation.GetTypeByMetadataName("XLua.LuaFunction");
            
            if (mLuaFunctionTypeSymbol == null)
            {
                throw new CodeGenException("mLuaFunctionTypeSymbol empty");
            }

            mXLuaClassProxyAdapterTypeSymbol = mCompilation.GetTypeByMetadataName("XLuaClassProxyAdapter");
            if (mXLuaClassProxyAdapterTypeSymbol==null)
            {
                throw new CodeGenException("mXLuaClassProxyAdapterTypeSymbol empty");
            }

        }

        public void GenCode()
        {
            bool reGenXLuaCode = false;

            foreach (var item in mCodeGenConfig.ProxyClassItems)
            {
                Console.WriteLine("生成{0}代理类中", item.TargetName);

                ProxyClassGenContext context = new ProxyClassGenContext();

                if (InitProxyClassGenContext(context, item))
                {
                    PerformProxyClassGeneration(context);

                    if (context.DelegateCallMethodSymbols.Count != 0)
                    {
                        reGenXLuaCode = true;
                    }
                }
            }

            foreach (var item in mCodeGenConfig.InterfaceItems)
            {
                Console.WriteLine("生成{0} Interface中", item.TargetName);

                InterfaceGenContext context = new InterfaceGenContext();

                if (InitInterfaceGenContext(context, item))
                {
                    PerformInterfaceGeneration(context);
                }
            }

            Console.Write("代码生成完毕");

            if (reGenXLuaCode)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("，注意，请在UnityEditor中执行XLua/Generate Code");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine();
            }
        }

        private bool InitInterfaceGenContext(InterfaceGenContext context, GenInterface item)
        {
            context.TargetName = item.TargetName;
            context.TargetDir = item.TargetDir;
            context.TypeSymbol = mCompilation.GetTypeByMetadataName(item.FullyQualifiedMetadataName);

            if (context.TypeSymbol == null)
            {
                Console.WriteLine("Cannot get type from FullyQualifiedMetadataName " + item.FullyQualifiedMetadataName);
                return false;
            }

            context.SyntaxGenerator = SyntaxGenerator.GetGenerator(mProject);

            context.MethodSymbols.AddRange(GetPublicMethodForGeneration(context.TypeSymbol));

            context.NormalMethodSymbols.AddRange(context.MethodSymbols.Where((sym) =>
            {
                return sym.MethodKind == MethodKind.Ordinary && !IsMethodImplementedForInterfaceMember(sym);
            }));

            context.PropertySymbols.AddRange(context.MethodSymbols.Where((sym) => !IsMethodImplementedForInterfaceMember(sym) && (sym.MethodKind == MethodKind.PropertyGet || sym.MethodKind == MethodKind.PropertySet))
                .Select((sym) => sym.AssociatedSymbol)
                .Distinct()
                .OfType<IPropertySymbol>()
                .ToArray());

            return true;
        }

        public void PerformInterfaceGeneration(InterfaceGenContext context)
        {
            var syntaxGen = context.SyntaxGenerator;
            var typeSymbol = context.TypeSymbol;

            var normalMethodNodes = new List<SyntaxNode>(context.NormalMethodSymbols.Count);

            foreach (var method in context.NormalMethodSymbols)
            {
                var methodDeclaration = syntaxGen.MethodDeclaration(method);
                normalMethodNodes.Add(methodDeclaration);
            }

            var propertyMethodNodes = new List<SyntaxNode>(context.PropertySymbols.Count);

            foreach (var p in context.PropertySymbols)
            {
                var propertyDeclaration = syntaxGen.PropertyDeclaration(p) as PropertyDeclarationSyntax;
                propertyMethodNodes.Add(propertyDeclaration);
            }

            var members = propertyMethodNodes.Union(normalMethodNodes);

            var interfaceDefinition = syntaxGen.InterfaceDeclaration(context.TargetName,
                accessibility: typeSymbol.DeclaredAccessibility,
                members: members);

            var namespaceDeclaration = syntaxGen.NamespaceDeclaration(typeSymbol.ContainingNamespace.Name, interfaceDefinition);
            var usingDirective = syntaxGen.NamespaceImportDeclaration("System");

            usingDirective = AddComment(usingDirective);

            var finalNode = syntaxGen.CompilationUnit(usingDirective, namespaceDeclaration).NormalizeWhitespace();

            var path = Path.Combine(context.TargetDir, context.TargetName);
            path = Path.ChangeExtension(path, ".cs");

            var str = SimplifyGeneratedCode(context.TargetName, finalNode);

            using (var sw = File.CreateText(path))
            {
                sw.Write(str);
            }
        }

        public bool InitProxyClassGenContext(ProxyClassGenContext context, GenProxyClass item)
        {
            context.TargetName = item.TargetName;
            context.TargetDir = item.TargetDir;
            context.TypeSymbol = mCompilation.GetTypeByMetadataName(item.FullyQualifiedMetadataName);

            if (context.TypeSymbol == null)
            {
                Console.WriteLine("Cannot get type from FullyQualifiedMetadataName " + item.FullyQualifiedMetadataName);
                return false;
            }

            context.SyntaxGenerator = SyntaxGenerator.GetGenerator(mProject);

            context.MethodSymbols.AddRange(GetPublicMethodForGeneration(context.TypeSymbol));

            context.NormalMethodSymbols = context.MethodSymbols.Where((sym) =>
            {
                if (sym.MethodKind == MethodKind.Ordinary)
                {
                    foreach (var p in sym.Parameters)
                    {
                        if (p.RefKind == RefKind.Out || p.RefKind == RefKind.Ref)
                        {
                            return false;
                        }
                    }

                    return true;
                }
                else
                {
                    return false;
                }
                
            }).ToList();

            context.PropertySymbols = context.MethodSymbols.Where((sym) =>
            {
                return sym.MethodKind == MethodKind.PropertyGet || sym.MethodKind == MethodKind.PropertySet;
            })
            .Select((a) => a.AssociatedSymbol)
            .Distinct()
            .OfType<IPropertySymbol>()
            .ToList();

            context.DelegateCallMethodSymbols = context.MethodSymbols.Where((sym) =>
            {
                if (sym.MethodKind == MethodKind.Ordinary)
                {
                    foreach (var p in sym.Parameters)
                    {
                        if (p.RefKind == RefKind.Out || p.RefKind == RefKind.Ref)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }).ToList();

            foreach (var sym in context.MethodSymbols)
            {
                context.NameDic[sym] = GetCs2LuaFunctionName(sym);
            }

            return true;
        }

        public void PerformProxyClassGeneration(ProxyClassGenContext context)
        {
            var typeSymbol = context.TypeSymbol;
            var syntaxGen = context.SyntaxGenerator;

            // Generate using
            var usingDirectives = new SyntaxNode[] { syntaxGen.NamespaceImportDeclaration("System"), syntaxGen.NamespaceImportDeclaration("XLua") };

            // Generate normal method
            var normalMethodNodes = new List<SyntaxNode>(context.NormalMethodSymbols.Count);

            foreach (IMethodSymbol method in context.NormalMethodSymbols)
            {
                normalMethodNodes.Add(GenerateNormalMethod(context, method, typeSymbol, syntaxGen));
            }
                                                          
            // Generate delegate call
            var delegateNodes = new List<Tuple<SyntaxNode, SyntaxNode, IMethodSymbol>>();

            foreach (var method in context.DelegateCallMethodSymbols)
            {
                var delegateNode = GenerateDelegateNode(context, method);
                var delegateCallMethodNode = GenerateDelegateCallMethod(context, method);

                delegateNodes.Add(new Tuple<SyntaxNode, SyntaxNode, IMethodSymbol>(delegateNode, delegateCallMethodNode, method));
            }

            // Generate property
            List<SyntaxNode> propertyNodes = new List<SyntaxNode>(context.PropertySymbols.Count);

            foreach (var propertySymbol in context.PropertySymbols)
            {
                propertyNodes.Add(GenerateProperty(context, propertySymbol, syntaxGen));
            }

            // Generate InitLuaFunction/UninitLuaFunction
            normalMethodNodes.Add(GenerateInitLuaFunction(context, typeSymbol, syntaxGen, delegateNodes));
            normalMethodNodes.Add(GenerateUninitLuaFunction(context, typeSymbol, syntaxGen, delegateNodes));
            normalMethodNodes.Add(GenerateGetLuaClassName(typeSymbol, syntaxGen));

            // Generate private fields
            var privateFields = GeneratePrivateFields(context, syntaxGen, typeSymbol, delegateNodes);

            //Generate XLuaClassProxyAdapter
            var luaAdpaterPublicField=syntaxGen.FieldDeclaration("mXLuaClassProxyAdapter",
                                                     syntaxGen.TypeExpression(mXLuaClassProxyAdapterTypeSymbol),
                                                     Accessibility.Private);
            var getXLuaProxyAdatperFunction = GenerateGetXLuaAdapterFunction(context,syntaxGen);

            var generaterConstructFunction = GeneraterConstructFunction(context,syntaxGen);

            // Union all syntax node
            var members = new List<SyntaxNode>();
            members.Add(luaAdpaterPublicField);
            members.Add(generaterConstructFunction);
            members.Add(getXLuaProxyAdatperFunction); 
            members.AddRange(delegateNodes.Select(a => a.Item1));
            members.AddRange(delegateNodes.Select(a => a.Item2));
            members.AddRange(propertyNodes);
            members.AddRange(normalMethodNodes);
            members.AddRange(privateFields);
            

            // Class
            var classDefinition = syntaxGen.ClassDeclaration(context.TargetName,
                                                             typeParameters: null,
                                                             accessibility: typeSymbol.DeclaredAccessibility,
                                                             modifiers: DeclarationModifiers.From(typeSymbol),
                                                             baseType: null,
                                                             interfaceTypes: null,
                                                             members: members);

            // Add base class
            //classDefinition = syntaxGen.AddBaseType(classDefinition, GetBaseNodeForGeneration(typeSymbol, syntaxGen));

            //Implements custom Interface IXLuaSystemAction
            classDefinition = syntaxGen.AddInterfaceType(classDefinition,syntaxGen.IdentifierName("IXLuaSystemAction"));
            if (typeSymbol.Interfaces != null)
            {
                var interfaceTypes = GetInterfaceNodeForGeneration(typeSymbol, syntaxGen);
                foreach (var @interface in interfaceTypes)
                {
                    classDefinition = syntaxGen.AddInterfaceType(classDefinition, @interface);
                }
            }

            classDefinition = syntaxGen.AddAttributes(classDefinition, syntaxGen.Attribute(syntaxGen.QualifiedName(syntaxGen.IdentifierName("Cs2Lua"), syntaxGen.IdentifierName("Ignore"))));

            SyntaxNode namespaceDeclaration = null;

            if (!string.IsNullOrEmpty(typeSymbol.ContainingNamespace.Name))
            {
                namespaceDeclaration = syntaxGen.NamespaceDeclaration(typeSymbol.ContainingNamespace.Name, classDefinition);
            }

            // add comment
            usingDirectives[0] = AddComment(usingDirectives[0]);

            var finalNode = syntaxGen.CompilationUnit(usingDirectives.Union(new SyntaxNode[] { namespaceDeclaration ?? classDefinition })).NormalizeWhitespace();

            var path = Path.Combine(context.TargetDir, context.TargetName);
            path = Path.ChangeExtension(path, ".cs");

            var str = SimplifyGeneratedCode(context.TargetName, finalNode);

            using (var sw = File.CreateText(path))
            {
                sw.Write(str);
            }
        }

        private string SimplifyGeneratedCode(string docName, SyntaxNode node)
        {
            // Generate document to simplify generated code
            var document = mProject.AddDocument(docName, node.GetText());
            var annotatedDocument = document.WithSyntaxRoot(document.GetSyntaxRootAsync().Result.WithAdditionalAnnotations(Simplifier.Annotation));
            var reducedDocument = Simplifier.ReduceAsync(annotatedDocument).Result;

            var reducedDocumentText = reducedDocument.GetTextAsync().Result;
            return reducedDocumentText.ToString();
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

        public void Dispose()
        {
            if (mWorkspace != null)
            {
                mWorkspace.Dispose();
            }
        }

        #region Generator

        public IMethodSymbol[] GetPublicMethodForGeneration(INamedTypeSymbol typeSymbol)
        {
            ImmutableArray<ISymbol> immutablearr = typeSymbol.GetMembers();

            return immutablearr.Where(sym =>
            {
                if (sym.Kind == SymbolKind.Method)
                {
                    if (sym.DeclaredAccessibility != Accessibility.Public)
                    {
                        return false;
                    }

                    var methodSymbol = sym as IMethodSymbol;

                    var attributes = methodSymbol.GetAttributes();

                    foreach (var attr in attributes)
                    {
                        if (attr.AttributeClass.Name == "IgnoreAttribute" && attr.AttributeClass.ContainingNamespace.Name == "Cs2Lua")
                        {
                            return false;
                        }
                    }

                    return methodSymbol.MethodKind == MethodKind.Ordinary || methodSymbol.MethodKind == MethodKind.PropertyGet || methodSymbol.MethodKind == MethodKind.PropertySet;
                }

                return false;
            }).OfType<IMethodSymbol>().ToArray();
        }

        public SyntaxNode GenerateDelegateNode(ProxyClassGenContext context, IMethodSymbol method)
        {
            var delegateName = GetDelegateNameForGeneration(context, method);
            var typeParameters = (method.TypeParameters != null && method.TypeParameters.Length > 0) ? method.TypeParameters.Select(a => a.Name) : null;

            var parameters = new List<SyntaxNode>();
            parameters.Add(context.SyntaxGenerator.ParameterDeclaration("self", context.SyntaxGenerator.TypeExpression(mLuaTableTypeSymbol)));
            parameters.AddRange(context.SyntaxGenerator.GetParameters(method.DeclaringSyntaxReferences.First().GetSyntax()));

            var delegateNode = context.SyntaxGenerator.DelegateDeclaration(delegateName,
                parameters: parameters,
                typeParameters: typeParameters,
                returnType: context.SyntaxGenerator.TypeExpression(method.ReturnType),
                accessibility: method.DeclaredAccessibility);

            delegateNode = context.SyntaxGenerator.AddAttributes(delegateNode, context.SyntaxGenerator.Attribute(context.SyntaxGenerator.IdentifierName("CSharpCallLua")));

            return delegateNode;
        }

        public SyntaxNode[] GeneratePrivateFields(ProxyClassGenContext context, SyntaxGenerator syntaxGen, INamedTypeSymbol typeSymbol, List<Tuple<SyntaxNode, SyntaxNode, IMethodSymbol>> delegateNodes)
        {
            List<SyntaxNode> nodes = new List<SyntaxNode>();

            foreach (var method in context.MethodSymbols.Except(context.DelegateCallMethodSymbols))
            {
                nodes.Add(syntaxGen.FieldDeclaration(GetPrivateVariableNameForGeneration(context, method),
                                                     syntaxGen.TypeExpression(mLuaFunctionTypeSymbol),
                                                     Accessibility.Private));
            }

            foreach (var node in delegateNodes)
            {
                nodes.Add(syntaxGen.FieldDeclaration(GetPrivateVariableNameForGeneration(context, node.Item3),
                                                     syntaxGen.IdentifierName(GetDelegateNameForGeneration(context, node.Item3)),
                                                     Accessibility.Private));
            }

            return nodes.ToArray();
        }

        public SyntaxNode GenerateDelegateCallMethod(ProxyClassGenContext context, IMethodSymbol methodSymbol)
        {
            SyntaxNode statements = GenerateMethodBody(context, methodSymbol, context.SyntaxGenerator, true);
            var methodDeclaration = context.SyntaxGenerator.MethodDeclaration(methodSymbol, new SyntaxNode[] { statements });
            return methodDeclaration;
        }

        public SyntaxNode GenerateNormalMethod(ProxyClassGenContext context, IMethodSymbol methodSymbol, INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen)
        {
            SyntaxNode statements = GenerateMethodBody(context, methodSymbol, syntaxGen, false);
            var methodDeclaration = syntaxGen.MethodDeclaration(methodSymbol, new SyntaxNode[] { statements });
            return methodDeclaration;
        }

        private SyntaxNode GenerateMethodBody(ProxyClassGenContext context, IMethodSymbol methodSymbol, SyntaxGenerator syntaxGen, bool delegateCall)
        {
            var variableIdentifier = syntaxGen.IdentifierName(GetPrivateVariableNameForGeneration(context, methodSymbol));
            var conditionStatement = syntaxGen.ValueNotEqualsExpression(variableIdentifier, syntaxGen.LiteralExpression(SyntaxKind.NullLiteralExpression));
            var falseStatement = syntaxGen.ThrowStatement(syntaxGen.ObjectCreationExpression(syntaxGen.TypeExpression(mSystemExceptionTypeSymbol), syntaxGen.LiteralExpression(variableIdentifier.ToString() + " NULL")));

            SyntaxNode invocationExpression = null;

            if (!delegateCall)
            {
                var genericName = GenerateLuaFunctionGenericName(methodSymbol, syntaxGen);
                var memberAccessExpression = syntaxGen.MemberAccessExpression(variableIdentifier, genericName);
                invocationExpression = syntaxGen.InvocationExpression(memberAccessExpression, GetMethodArgumentsForGeneration(methodSymbol, syntaxGen, false, true));
            }
            else
            {
                invocationExpression = syntaxGen.InvocationExpression(variableIdentifier, GetMethodArgumentsForGeneration(methodSymbol, syntaxGen, true, true));
            }

            SyntaxNode trueStatement = null;

            if (!methodSymbol.ReturnsVoid)
            {
                trueStatement = syntaxGen.ReturnStatement(invocationExpression);
            }
            else
            {
                trueStatement = invocationExpression;
            }

            var ifStatement = syntaxGen.IfStatement(conditionStatement, new SyntaxNode[] { trueStatement }, new SyntaxNode[] { falseStatement });
            return ifStatement;
        }

        public SyntaxNode GenerateProperty(ProxyClassGenContext context, IPropertySymbol propertySymbol, SyntaxGenerator syntaxGen)
        {
            SyntaxNode getMethodNode = null;
            SyntaxNode setMethodNode = null;

            if (propertySymbol.GetMethod != null)
            {
                getMethodNode = GenerateMethodBody(context, propertySymbol.GetMethod, syntaxGen, false);
            }

            if (propertySymbol.SetMethod != null)
            {
                setMethodNode = GenerateMethodBody(context, propertySymbol.SetMethod, syntaxGen, false);
            }

            var propertyNode = syntaxGen.PropertyDeclaration(propertySymbol,
                                                             (getMethodNode != null ? new SyntaxNode[] { getMethodNode } : null),
                                                             (setMethodNode != null ? new SyntaxNode[] { setMethodNode } : null));
            return propertyNode;
        }

        private SyntaxNode GenerateLuaFunctionGenericName(IMethodSymbol methodSymbol, SyntaxGenerator syntaxGen)
        {
            if (methodSymbol.ReturnsVoid)
            {
                return syntaxGen.GenericName("Action", GetMethodParameterTypesForGeneration(methodSymbol));
            }
            else
            {
                return syntaxGen.GenericName("Func", GetMethodParameterTypesForGeneration(methodSymbol));
            }
        }

        private SyntaxNode GeneraterConstructFunction(ProxyClassGenContext context, SyntaxGenerator syntaxGen)
        {
    
            List<SyntaxNode> augurments = new List<SyntaxNode>();

            augurments.Add(syntaxGen.ThisExpression());

            List<SyntaxNode> statements = new List<SyntaxNode>();
            statements.Add(syntaxGen.AssignmentStatement(syntaxGen.IdentifierName("mXLuaClassProxyAdapter"), syntaxGen.ObjectCreationExpression(SyntaxFactory.IdentifierName("XLuaClassProxyAdapter"),augurments)));
            
            return syntaxGen.ConstructorDeclaration(context.TargetName,null,Accessibility.Public,DeclarationModifiers.None,null,statements);
        }

        private SyntaxNode GenerateGetXLuaAdapterFunction(ProxyClassGenContext context, SyntaxGenerator syntaxGen)
        {
            List<SyntaxNode> statements = new List<SyntaxNode>();
            statements.Add(syntaxGen.ReturnStatement(syntaxGen.IdentifierName("mXLuaClassProxyAdapter")));
            return syntaxGen.MethodDeclaration("getXLuaClassProxyAdapter",null,null, syntaxGen.TypeExpression(mXLuaClassProxyAdapterTypeSymbol), Accessibility.Public,DeclarationModifiers.None,statements);
        }

        private SyntaxNode getSelfField(SyntaxGenerator syntaxGen)
        {
            //return syntaxGen.IdentifierName("Self");
            var variableIdentifier = syntaxGen.IdentifierName("mXLuaClassProxyAdapter");
            return syntaxGen.InvocationExpression(syntaxGen.MemberAccessExpression(variableIdentifier, syntaxGen.IdentifierName("GetLuaTableSelf")));
        }

        private SyntaxNode GenerateInitLuaFunction(ProxyClassGenContext context, INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen, List<Tuple<SyntaxNode, SyntaxNode, IMethodSymbol>> delegateNodes)
        {
            List<SyntaxNode> statements = new List<SyntaxNode>();

            foreach (var method in context.MethodSymbols.Except(context.DelegateCallMethodSymbols))
            {
                var genericName = syntaxGen.GenericName("GetInPath", mLuaFunctionTypeSymbol);
                var memberAccessExpression = syntaxGen.MemberAccessExpression(getSelfField(syntaxGen), genericName);
                var invocationExpression = syntaxGen.InvocationExpression(memberAccessExpression, syntaxGen.LiteralExpression(context.NameDic[method]));
                var assignExpression = syntaxGen.AssignmentStatement(syntaxGen.IdentifierName(GetPrivateVariableNameForGeneration(context, method)), invocationExpression);
                statements.Add(assignExpression);
            }

            foreach (var node in delegateNodes)
            {
                var genericName = syntaxGen.GenericName("Get", syntaxGen.IdentifierName(GetDelegateNameForGeneration(context, node.Item3)));
                var memberAccessExpression = syntaxGen.MemberAccessExpression(getSelfField(syntaxGen), genericName);
                var invocationExpression = syntaxGen.InvocationExpression(memberAccessExpression, syntaxGen.LiteralExpression(context.NameDic[node.Item3]));
                var assignExpression = syntaxGen.AssignmentStatement(syntaxGen.IdentifierName(GetPrivateVariableNameForGeneration(context, node.Item3)), invocationExpression);
                statements.Add(assignExpression);
            }

            return syntaxGen.MethodDeclaration("InitLuaFunction", null, null, null, Accessibility.Public, DeclarationModifiers.None, statements);
        }

        private SyntaxNode GenerateUninitLuaFunction(ProxyClassGenContext context, INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen, List<Tuple<SyntaxNode, SyntaxNode, IMethodSymbol>> delegateNodes)
        {
            var statements = new List<SyntaxNode>();

            foreach (var method in context.MethodSymbols.Except(context.DelegateCallMethodSymbols))
            {
                var variableIdentifier = syntaxGen.IdentifierName(GetPrivateVariableNameForGeneration(context, method));
                var conditionStatement = syntaxGen.ValueNotEqualsExpression(variableIdentifier, syntaxGen.LiteralExpression(SyntaxKind.NullLiteralExpression));
                var memberAccessExpression = syntaxGen.MemberAccessExpression(variableIdentifier, syntaxGen.IdentifierName("Dispose"));
                var invocationExpression = syntaxGen.InvocationExpression(memberAccessExpression);
                var ifStatement = syntaxGen.IfStatement(conditionStatement, new SyntaxNode[] { invocationExpression });

                statements.Add(ifStatement);
            }

            return syntaxGen.MethodDeclaration("UninitLuaFunction", null, null, null, Accessibility.Public, DeclarationModifiers.None, statements);
        }

        private SyntaxNode GenerateGetLuaClassName(INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen)
        {
            string fullNamespaceName = GetFullNamespace(typeSymbol);
            string name = fullNamespaceName + "." + typeSymbol.Name;
            var returnStatement = syntaxGen.ReturnStatement(syntaxGen.LiteralExpression(name));
            return syntaxGen.MethodDeclaration("GetLuaClassName", null, null, syntaxGen.TypeExpression(SpecialType.System_String), Accessibility.Public, DeclarationModifiers.None, new SyntaxNode[] { returnStatement });
        }

        private ITypeSymbol[] GetMethodParameterTypesForGeneration(IMethodSymbol methodSymbol)
        {
            List<ITypeSymbol> typeSymbols = new List<ITypeSymbol>();

            typeSymbols.Add(mLuaTableTypeSymbol);

            foreach (var item in methodSymbol.Parameters)
            {
                typeSymbols.Add(item.Type);
            }

            if (!methodSymbol.ReturnsVoid)
            {
                // Add return type
                typeSymbols.Add(methodSymbol.ReturnType);
            }

            return typeSymbols.ToArray();
        }

        private SyntaxNode[] GetMethodArgumentsForGeneration(IMethodSymbol methodSymbol, SyntaxGenerator syntaxGen, bool addRefOrOut, bool addSelf)
        {
            List<SyntaxNode> nodes = new List<SyntaxNode>();

            if (addSelf)
            {
                nodes.Add(getSelfField(syntaxGen));
            }

            foreach (var parameter in methodSymbol.Parameters)
            {
                nodes.Add(syntaxGen.Argument(addRefOrOut ? parameter.RefKind : RefKind.None, syntaxGen.IdentifierName(parameter.Name)));
            }

            return nodes.ToArray();
        }

        private SyntaxNode[] GetInterfaceNodeForGeneration(INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen)
        {
            List<SyntaxNode> node = new List<SyntaxNode>();

            foreach (var item in typeSymbol.Interfaces)
            {
                node.Add(syntaxGen.IdentifierName(item.Name));
            }

            return node.ToArray();
        }

        private SyntaxNode GetBaseNodeForGeneration(INamedTypeSymbol typeSymbol, SyntaxGenerator syntaxGen)
        {
            return syntaxGen.IdentifierName("XLuaClassProxyBase");
        }

        public static string GetFullNamespace(ISymbol symbol)
        {
            if ((symbol.ContainingNamespace == null) ||
                 (string.IsNullOrEmpty(symbol.ContainingNamespace.Name)))
            {
                return null;
            }

            // get the rest of the full namespace string
            string restOfResult = GetFullNamespace(symbol.ContainingNamespace);

            string result = symbol.ContainingNamespace.Name;

            if (restOfResult != null)
                // if restOfResult is not null, append it after a period
                result = restOfResult + '.' + result;

            return result;
        }

        private string GetPrivateVariableNameForGeneration(ProxyClassGenContext context, IMethodSymbol symbol)
        {
            return "m_" + context.NameDic[symbol];
        }

        private string GetDelegateNameForGeneration(ProxyClassGenContext context, IMethodSymbol symbol)
        {
            return "Delegate_" + context.NameDic[symbol];
        }

        private string GetCs2LuaFunctionName(IMethodSymbol symbol)
        {
            if (symbol.ContainingType.GetMembers(symbol.Name).Count() > 1)
            {
                return CalcMethodMangling(symbol);
            }
            else
            {
                return symbol.Name;
            }
        }

        private bool IsMethodImplementedForInterfaceMember(IMethodSymbol symbol)
        {
            if (mCodeGenConfig.DontGenerateMethodImplementForInterfaceMember)
            {
                return false;
            }

            var typeSymbol = symbol.ContainingType;

            bool result = false;

            foreach (var interfaceSym in typeSymbol.AllInterfaces)
            {
                foreach (var interfaceMember in interfaceSym.GetMembers())
                {
                    var resultSym = typeSymbol.FindImplementationForInterfaceMember(interfaceMember);
                    if (symbol.Equals(resultSym))
                    {
                        result = true;
                        break;
                    }
                }                
            }

            return result;
        }

        #endregion

        #region Cs2lua Code
        internal static string CalcMethodMangling(IMethodSymbol methodSym)
        {
            if (null == methodSym)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            string name = GetMethodName(methodSym);
            if (!string.IsNullOrEmpty(name) && name[0] == '.')
                name = name.Substring(1);
            sb.Append(name);

            {
                foreach (var param in methodSym.Parameters)
                {
                    sb.Append("__");
                    if (param.RefKind == RefKind.Ref)
                    {
                        sb.Append("Ref_");
                    }
                    else if (param.RefKind == RefKind.Out)
                    {
                        sb.Append("Out_");
                    }
                    var oriparam = param.OriginalDefinition;
                    if (oriparam.Type.Kind == SymbolKind.ArrayType)
                    {
                        sb.Append("Arr_");
                        var arrSym = oriparam.Type as IArrayTypeSymbol;
                        string fn;
                        if (arrSym.ElementType.TypeKind == TypeKind.TypeParameter)
                        {
                            fn = GetFullNameWithTypeParameters(arrSym.ElementType);
                        }
                        else
                        {
                            fn = GetFullName(arrSym.ElementType);
                        }
                        sb.Append(fn.Replace('.', '_'));
                    }
                    else if (oriparam.Type.TypeKind == TypeKind.TypeParameter)
                    {
                        string fn = GetFullNameWithTypeParameters(oriparam.Type);
                        sb.Append(fn.Replace('.', '_'));
                    }
                    else
                    {
                        string fn = GetFullName(oriparam.Type);
                        sb.Append(fn.Replace('.', '_'));
                    }
                }
            }
            return sb.ToString();
        }
        internal static string GetMethodName(IMethodSymbol sym)
        {
            if (null == sym)
            {
                return string.Empty;
            }
            if (sym.ExplicitInterfaceImplementations.Length > 0)
            {
                int ix = sym.Name.LastIndexOf('.');
                return CalcNameWithFullTypeName(sym.Name.Substring(ix + 1), sym.ContainingType);
            }
            else
            {
                return sym.Name;
            }
        }
        internal static string CalcNameWithFullTypeName(string name, INamedTypeSymbol typeSym)
        {
            if (null == typeSym)
            {
                return name;
            }
            else
            {
                string ns = CalcNameWithTypeParameters(typeSym);
                if (string.IsNullOrEmpty(ns))
                {
                    return name;
                }
                else
                {
                    return ns.Replace(".", "_") + "_" + name;
                }
            }
        }
        internal static string GetFullNameWithTypeParameters(ISymbol type)
        {
            if (null == type)
                return string.Empty;
            //if (SymbolTable.Instance.IsCs2LuaSymbol(type))
            //{
            //    return CalcFullNameWithTypeParameters(type, true);
            //}
            //else
            {
                return PrefixExternClassName(CalcFullNameWithTypeParameters(type, true));
            }
        }
        internal static string PrefixExternClassName(string cn)
        {
            return "CS." + cn;
        }
        private static string CalcFullNameWithTypeParameters(ISymbol type, bool includeSelfName)
        {
            if (null == type)
                return string.Empty;
            List<string> list = new List<string>();
            if (includeSelfName)
            {
                list.Add(CalcNameWithTypeParameters(type));
            }
            INamespaceSymbol ns = type.ContainingNamespace;
            var ct = type.ContainingType;
            string name = string.Empty;
            if (null != ct)
            {
                name = CalcNameWithTypeParameters(ct);
            }
            while (null != ct && name.Length > 0)
            {
                list.Insert(0, name);
                ns = ct.ContainingNamespace;
                ct = ct.ContainingType;
                if (null != ct)
                {
                    name = CalcNameWithTypeParameters(ct);
                }
                else
                {
                    name = string.Empty;
                }
            }
            while (null != ns && ns.Name.Length > 0)
            {
                list.Insert(0, ns.Name);
                ns = ns.ContainingNamespace;
            }
            return string.Join(".", list.ToArray());
        }

        private static string CalcNameWithTypeParameters(ISymbol sym)
        {
            if (null == sym)
                return string.Empty;
            var typeSym = sym as INamedTypeSymbol;
            if (null != typeSym)
            {
                return CalcNameWithTypeParameters(typeSym);
            }
            else
            {
                return sym.Name;
            }
        }
        private static string CalcNameWithTypeParameters(INamedTypeSymbol type)
        {
            if (null == type)
                return string.Empty;
            List<string> list = new List<string>();
            list.Add(type.Name);
            foreach (var param in type.TypeParameters)
            {
                list.Add(param.Name);
            }
            return string.Join("_", list.ToArray());
        }
        internal static string GetFullName(ISymbol type)
        {
            if (null == type)
                return string.Empty;
            //if (SymbolTable.Instance.IsCs2LuaSymbol(type))
            //{
            //    return CalcFullName(type, true);
            //}
            //else
            {
                //外部类型不会基于泛型样式导入，只有使用lua实现的集合类会出现这种情况，这里需要用泛型类型名以与utility.lua里的名称一致
                return PrefixExternClassName(CalcFullNameWithTypeParameters(type, true));
            }
        }
        #endregion

        private CodeGenConfig mCodeGenConfig;
        private MSBuildWorkspace mWorkspace;
        private Project mProject;
        private CSharpCompilation mCompilation;

        private ITypeSymbol mLuaTableTypeSymbol;
        private ITypeSymbol mLuaFunctionTypeSymbol;
        private ITypeSymbol mSystemExceptionTypeSymbol;
        private ITypeSymbol mXLuaClassProxyAdapterTypeSymbol;

        private IResolveReference mReferenceResolver;
    }
}
