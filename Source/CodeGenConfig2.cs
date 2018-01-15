using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace CodeGen.Source
{
    public class Test
    {
        public void foo()
        {

        }

        private int mVal = 0;
    }


    public class GenProxyClass
    {
        [XmlAttribute("fullyQualifiedMetadataName")]
        public string FullyQualifiedMetadataName;

        [XmlAttribute("targetName")]
        public string TargetName;

        [XmlAttribute("targetDir")]
        public string TargetDir;

        [XmlAttribute("methodDeclaration")]
        public string MethodDeclaration;

        [XmlAttribute("accessibility")]
        public string Accessibility;

        [XmlAttribute("returnType")]
        public string ReturnType;

        [XmlAttribute("fieldDeclaration")]
        public string FieldDeclaration;
    }

    [XmlRoot("CodeGenConfig2")]
    public class CodeGenConfig2
    {
        [XmlAttribute("DontGenerateComment")]
        public bool DontGenerateComment;

        [XmlElement("Project")]
        public string Project;

        [XmlElement("SystemDllPath")]
        public string SystemDllPath;

        [XmlArray("GenProxyClass")]
        [XmlArrayItem("ProxyClass")]
        public List<GenProxyClass> ProxyClassItems;

        public static CodeGenConfig2 Load(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open))
                {
                    var serialier = new XmlSerializer(typeof(CodeGenConfig2));
                    return serialier.Deserialize(fs) as CodeGenConfig2;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return null;
            }
        }
    }
}
