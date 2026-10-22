using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Starshot.Features.Subtitles;

internal sealed record SubtitleCue(int Id,string Text);
internal sealed record SubtitleTranscript(int Revision,string Text,SubtitleCue[] Recent,int Count);

/// <summary>Caption episodes, not a global text blacklist. The owner serializes access.</summary>
internal sealed class SubtitleCollector
{
    private readonly List<SubtitleCue> _cues=[];
    private string[] _candidate=[],_visible=[];
    private string _candidateKey="",_visibleKey="";
    private int _stable;
    private string _skippedPrefix="";
    private long? _blankSince;
    private int _revision;
    internal int Revision=>_revision;
    private static string Key(string text)=>Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(),@"\s+"," ");
    private static bool Cjk(char c)=>c is >= '\u2e80' and <= '\u9fff' or >= '\u3040' and <= '\u30ff';
    private static string Join(IEnumerable<string> lines)
    {
        var text=new StringBuilder();
        foreach(string line in lines.Select(Key).Where(l=>l.Length>0))
        {if(text.Length>0&&!(Cjk(text[^1])&&Cjk(line[0])))text.Append(' ');text.Append(line);}
        return text.ToString();
    }
    internal void Observe(IEnumerable<string> lines,long elapsedMs)
    {
        var input=lines.Select(l=>l.Trim()).Where(l=>l.Length>0).Take(64).Select(l=>l.Length>2048?l[..2048]:l).ToArray();
        if(input.Length==0)
        {
            _blankSince??=elapsedMs;
            if(elapsedMs-_blankSince>=300)EndCaption();
            return;
        }
        if(_blankSince is{} blank&&elapsedMs-blank>=300)EndCaption();
        _blankSince=null;string key=Join(input);
        if(key==_candidateKey){_stable++;if(_stable>=2)Commit();return;}
        // A short caption seen only once must not vanish when the next caption arrives.
        Commit();_candidate=input;_candidateKey=key;_stable=1;
    }
    internal void EndCaption(){Commit();_candidate=[];_candidateKey="";_visible=[];_visibleKey="";_stable=0;_skippedPrefix="";}
    internal void Flush()=>Commit();
    private void Commit()
    {
        if(_candidateKey.Length==0||_candidateKey==_visibleKey)return;
        string[] output=_candidate;bool extend=_visibleKey.Length>0&&_candidateKey.StartsWith(_visibleKey,StringComparison.Ordinal)&&_cues.Count>0;
        int skip=0;
        if(!extend)
        {
            for(int n=Math.Min(_visible.Length,output.Length);n>0;n--)
                if(_visible.TakeLast(n).Select(Key).SequenceEqual(output.Take(n).Select(Key))){skip=n;break;}
        }
        string prefix=extend?_skippedPrefix:Join(output.Take(skip));
        // A rolling prefix can wrap into a different number of lines while the
        // current caption grows. Remove its canonical text, not its old line count.
        string text=extend&&prefix.Length>0?_candidateKey[prefix.Length..].TrimStart():string.Join(Environment.NewLine,output.Skip(skip));
        if(text.Length>0)
        {
            if(!extend&&_cues.Count>=20_000){_candidate=_visible;_candidateKey=_visibleKey;throw new InvalidOperationException("本次摘录已达 20000 段，请导出后新建任务。");}
            int length=_cues.Sum(c=>c.Text.Length)-(extend?_cues[^1].Text.Length:0)+text.Length;
            if(length>1_000_000){_candidate=_visible;_candidateKey=_visibleKey;throw new InvalidOperationException("本次摘录已达文本上限，请导出后新建任务。");}
            if(extend)_cues[^1]=_cues[^1] with{Text=text};else _cues.Add(new(_cues.Count+1,text));
            _revision++;
        }
        _visible=output;_visibleKey=_candidateKey;_skippedPrefix=prefix;
    }
    internal SubtitleTranscript Snapshot(bool includeText=true)=>new(_revision,includeText?string.Join(Environment.NewLine,_cues.Select(c=>c.Text)):"",_cues.TakeLast(5).ToArray(),_cues.Count);
}
