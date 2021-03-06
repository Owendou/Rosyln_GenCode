﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;
using CodeGen.Source;

namespace CodeGen
{
    class ResolveReferenceByRosyln : IResolveReference
    {
        private Project mProject;

        public ResolveReferenceByRosyln(Project project)
        {
            mProject = project;
        }

        CSharpCompilation IResolveReference.GetReferenceResolvedCompilation(CodeGenConfig config)
        {
            return mProject.GetCompilationAsync().Result as CSharpCompilation;
        }

        CSharpCompilation IResolveReference.GetReferenceResolvedCompilation(CodeGenConfig2 config)
        {
            return mProject.GetCompilationAsync().Result as CSharpCompilation;
        }
    }
}
