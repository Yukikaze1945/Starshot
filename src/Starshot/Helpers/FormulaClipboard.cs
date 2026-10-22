using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Starshot.Helpers;

internal static class FormulaClipboard
{
    internal static string Normalize(string mathml)
    {
        if(mathml.Length>500_000)throw new ArgumentException("公式过大。");
        using var reader=XmlReader.Create(new StringReader(mathml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=500_000});
        var root=XElement.Load(reader);const string ns="http://www.w3.org/1998/Math/MathML";
        if(root.Name!=XName.Get("math",ns))throw new ArgumentException("需要有效 Presentation MathML。");
        root.Descendants().Where(e=>e.Name.LocalName is "annotation" or "annotation-xml").Remove();
        string[] allowed=["math","semantics","mrow","mi","mn","mo","mtext","mspace","ms","mfrac","msqrt","mroot","mstyle","merror","mpadded","mphantom","mfenced","menclose","msub","msup","msubsup","munder","mover","munderover","mmultiscripts","mprescripts","none","mtable","mtr","mtd","mlabeledtr"];
        if(root.DescendantsAndSelf().Count()>10000||root.DescendantsAndSelf().Any(e=>e.Name.NamespaceName!=ns||!allowed.Contains(e.Name.LocalName)))throw new ArgumentException("MathML 包含不支持的节点。");
        if(root.DescendantsAndSelf().Attributes().Any(a=>!a.IsNamespaceDeclaration&&(a.Name.NamespaceName.Length>0||a.Name.LocalName.StartsWith("on",StringComparison.OrdinalIgnoreCase)||a.Name.LocalName is "href" or "src" or "style")))throw new ArgumentException("MathML 不允许外部资源或脚本属性。");
        return root.ToString(SaveOptions.DisableFormatting);
    }
    internal static void Copy(nint owner,string mathml,string latex)
    {
        string xml=Normalize(mathml);uint first=RegisterClipboardFormat("MathML"),second=RegisterClipboardFormat("MathML Presentation");
        if(first==0||second==0)throw new System.ComponentModel.Win32Exception();
        bool opened=false;for(int k=0;k<10;k++){if(OpenClipboard(owner)){opened=true;break;}Thread.Sleep(15);}
        if(!opened)throw new InvalidOperationException("剪贴板正忙，请重试。");
        try{if(!EmptyClipboard())throw new System.ComponentModel.Win32Exception();Put(first,xml);Put(second,xml);Put(13,latex);}
        finally{CloseClipboard();}
    }
    private static void Put(uint format,string value)
    {
        byte[] bytes=Encoding.Unicode.GetBytes(value+'\0');nint memory=GlobalAlloc(2,(nuint)bytes.Length);if(memory==0)throw new OutOfMemoryException();
        try{nint address=GlobalLock(memory);if(address==0)throw new System.ComponentModel.Win32Exception();try{Marshal.Copy(bytes,0,address,bytes.Length);}finally{GlobalUnlock(memory);}if(SetClipboardData(format,memory)==0)throw new System.ComponentModel.Win32Exception();memory=0;}
        finally{if(memory!=0)GlobalFree(memory);}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetClipboardData(uint format,nint memory);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint GlobalAlloc(uint flags,nuint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]private static extern nint GlobalFree(nint memory);
}
