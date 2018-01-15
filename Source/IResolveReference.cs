using CodeGen.Source;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeGen
{
    public interface IResolveReference
    {
        CSharpCompilation GetReferenceResolvedCompilation(CodeGenConfig config);
        CSharpCompilation GetReferenceResolvedCompilation(CodeGenConfig2 config);
    }

}