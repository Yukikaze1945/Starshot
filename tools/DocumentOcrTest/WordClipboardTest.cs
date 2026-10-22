using System.Runtime.InteropServices;
using Starshot.Helpers;

internal static class WordClipboardTest
{
    internal static Task<object> RunAsync()
    {
        var done=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            object? outcome=null;Exception? failure=null;
            object? application=null,document=null,range=null,maths=null;nint previous=0,owner=0;
            _=OleInitialize(0);
            try
            {
                var type=Type.GetTypeFromProgID("Word.Application")??throw new InvalidOperationException("Word automation is not installed.");
                _=OleGetClipboard(out previous);owner=CreateWindowEx(0,"STATIC","Starshot isolated formula test",0,0,0,0,0,new nint(-3),0,0,0);
                application=Activator.CreateInstance(type)!;dynamic word=application;word.Visible=false;word.DisplayAlerts=0;
                document=word.Documents.Add();dynamic doc=document;range=doc.Content;
                FormulaClipboard.Copy(owner,"<math xmlns=\"http://www.w3.org/1998/Math/MathML\" display=\"block\"><mfrac><msup><mi>x</mi><mn>2</mn></msup><msqrt><mi>y</mi></msqrt></mfrac></math>","\\frac{x^2}{\\sqrt{y}}");
                ((dynamic)range).Paste();maths=doc.OMaths;int count=((dynamic)maths).Count;string xml=doc.WordOpenXML;
                if(count!=1||!xml.Contains("<m:f>")||!xml.Contains("<m:rad>"))throw new InvalidOperationException("Word did not import a native editable fraction and radical.");
                outcome=new{ok=true,nativeEditableEquations=count,containsOmmlFraction=true,containsOmmlRadical=true};
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                try{if(document is not null)((dynamic)document).Close(0);}catch{}
                try{if(application is not null)((dynamic)application).Quit(0);}catch{}
                foreach(var value in new[]{maths,range,document,application})if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);
                if(previous!=0){_=OleSetClipboard(previous);Marshal.Release(previous);}else if(owner!=0){_=OpenClipboard(owner);_=EmptyClipboard();_=CloseClipboard();}
                if(owner!=0)DestroyWindow(owner);OleUninitialize();
            }
            if(failure is not null)done.TrySetException(failure);else done.TrySetResult(outcome!);
        });
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    [DllImport("ole32.dll")]private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern int OleGetClipboard(out nint data);
    [DllImport("ole32.dll")]private static extern int OleSetClipboard(nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")]private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
}
