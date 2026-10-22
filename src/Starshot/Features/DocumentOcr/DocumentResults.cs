using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Starshot.Features.DocumentOcr;

internal sealed record PixelBox(float X,float Y,float Width,float Height)
{
    public float Right=>X+Width;
    public float Bottom=>Y+Height;
    public float Area=>Math.Max(0,Width)*Math.Max(0,Height);
    public float Intersection(PixelBox b)=>Math.Max(0,Math.Min(Right,b.Right)-Math.Max(X,b.X))*Math.Max(0,Math.Min(Bottom,b.Bottom)-Math.Max(Y,b.Y));
}
internal sealed record FormulaResult(string Latex,bool Complete,int Tokens,long ElapsedMs);
internal sealed record TableCell(int Row,int Column,int RowSpan,int ColSpan,PixelBox? Box,string Text);
internal sealed record TableResult(int Rows,int Columns,List<TableCell> Cells,float Score)
{
    public string CoordinateSpace {get;init;}="input";
    public string Html()
    {
        var s=new StringBuilder("<table><tbody>");
        for(int r=0;r<Rows;r++){s.Append("<tr>");foreach(var c in Cells.Where(c=>c.Row==r).OrderBy(c=>c.Column))
            s.Append($"<td rowspan=\"{c.RowSpan}\" colspan=\"{c.ColSpan}\">{WebUtility.HtmlEncode(c.Text).Replace("\n","<br>")}</td>");s.Append("</tr>");}
        return s.Append("</tbody></table>").ToString();
    }
    public string Tsv()=>string.Join("\n",Enumerable.Range(0,Rows).Select(r=>string.Join("\t",Enumerable.Range(0,Columns).Select(col=>
        (Cells.FirstOrDefault(c=>c.Row==r&&c.Column==col)?.Text??"").Replace("\t"," ").Replace("\r","").Replace("\n"," ")))));
}
internal sealed record LayoutRegion(string Id,int ClassId,string Label,float Score,PixelBox Box)
{
    public string? ParentId {get;set;}
    public int Order {get;set;}
    public string State {get;set;}="pending";
    public string? Error {get;set;}
    public string Text {get;set;}="";
    public FormulaResult? Formula {get;set;}
    public TableResult? Table {get;set;}
    public bool IsFormula=>Label=="formula";
    public bool IsTable=>Label=="table";
    public bool IsImage=>Label is "image" or "chart" or "header_image" or "footer_image" or "seal";
}
internal sealed record PageRecognitionResult(string Id,int Width,int Height)
{
    public string State {get;set;}="running";
    public string? LayoutModel {get;set;}
    public string Stage {get;set;}="准备模型";
    public string? Error {get;set;}
    public long ElapsedMs {get;set;}
    public Dictionary<string,long> TimingsMs {get;set;}=[];
    public List<LayoutRegion> Regions {get;set;}=[];
}

internal static class DocumentReadingOrder
{
    // Recursive whitespace cuts: full-width headings are separated vertically,
    // then independently read columns. A split must not cross a region.
    internal static List<LayoutRegion> Sort(IEnumerable<LayoutRegion> input)
    {
        var all=input.ToList();var result=new List<LayoutRegion>();Walk(all,0);
        for(int i=0;i<result.Count;i++)result[i].Order=i+1;
        return result;
        void Walk(List<LayoutRegion> regions,int depth)
        {
            if(regions.Count<2||depth>32){result.AddRange(regions);return;}
            (float Gap,float Cut) Gap(bool horizontal)
            {
                var ordered=regions.OrderBy(r=>horizontal?r.Box.X:r.Box.Y).ToArray();float end=horizontal?ordered[0].Box.Right:ordered[0].Box.Bottom,best=0,cut=0;
                foreach(var r in ordered.Skip(1)){float start=horizontal?r.Box.X:r.Box.Y;if(start-end>best){best=start-end;cut=(start+end)/2;}end=Math.Max(end,horizontal?r.Box.Right:r.Box.Bottom);}
                return(best,cut);
            }
            var gx=Gap(true);var gy=Gap(false);
            bool x=gx.Gap>=12&&(gy.Gap<8||gx.Gap>gy.Gap*.7f);
            if(!x&&gy.Gap<4){result.AddRange(regions.OrderBy(r=>r.Box.Y).ThenBy(r=>r.Box.X));return;}
            float split=x?gx.Cut:gy.Cut;
            var a=regions.Where(r=>(x?r.Box.X:r.Box.Y)<split).ToList();var b=regions.Except(a).ToList();
            if(a.Count==0||b.Count==0){result.AddRange(regions.OrderBy(r=>r.Box.Y).ThenBy(r=>r.Box.X));return;}
            Walk(a,depth+1);Walk(b,depth+1);
        }
    }
}
