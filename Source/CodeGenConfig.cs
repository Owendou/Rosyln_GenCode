using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace CodeGen
{
    public class GenProxyClass
    {
        [XmlAttribute("fullyQualifiedMetadataName")]
        public string FullyQualifiedMetadataName;

        [XmlAttribute("targetName")]
        public string TargetName;

        [XmlAttribute("targetDir")]
        public string TargetDir;
    }

    public class GenInterface
    {
        [XmlAttribute("fullyQualifiedMetadataName")]
        public string FullyQualifiedMetadataName;

        [XmlAttribute("targetName")]
        public string TargetName;

        [XmlAttribute("targetDir")]
        public string TargetDir;
    }
    
    [XmlRoot("CodeGenConfig")]
    public class CodeGenConfig
    {
        [XmlAttribute("DontGenerateComment")]
        public bool DontGenerateComment;

        [XmlAttribute("DontGenerateMethodImplementForInterfaceMember")]
        public bool DontGenerateMethodImplementForInterfaceMember;

        [XmlElement("Project")]
        public string Project;

        [XmlElement("SystemDllPath")]
        public string SystemDllPath;

        [XmlArray("GenProxyClass")]
        [XmlArrayItem("ProxyClass")]
        public List<GenProxyClass> ProxyClassItems;

        [XmlArray("GenInterface")]
        [XmlArrayItem("Interface")]
        public List<GenInterface> InterfaceItems;

        [XmlElement("CheckReference")]
        public string Cs2LuaReferenceFilePath;
        
        [XmlAttribute("IsResolveReferenceManually")]
        public bool IsResolveReferenceManually;

        public static CodeGenConfig Load(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open))
                {
                    var serializer = new XmlSerializer(typeof(CodeGenConfig));
                    return serializer.Deserialize(fs) as CodeGenConfig;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }
    }
}
