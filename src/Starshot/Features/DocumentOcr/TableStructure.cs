using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Starshot.Features.DocumentOcr;

internal static class TableStructure
{
    internal static TableResult Parse(string html,IReadOnlyList<PixelBox> boxes,float score)
    {
        using var reader=XmlReader.Create(new StringReader(html),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=500_000});
        var doc=XDocument.Load(reader);var rows=doc.Descendants("tr").ToList();if(rows.Count is 0 or >1000)throw new InvalidDataException("表格没有有效行，或超出上限。");
        var cells=new List<TableCell>();var occupied=new HashSet<(int,int)>();int index=0,columns=0;
        for(int r=0;r<rows.Count;r++)
        {
            int col=0;foreach(var td in rows[r].Elements().Where(e=>e.Name.LocalName is "td" or "th"))
            {
                while(occupied.Contains((r,col)))col++;
                int Span(string name)=>int.TryParse((string?)td.Attribute(name),out int v)&&v is >0 and <=1000?v:1;
                int rs=Span("rowspan"),cs=Span("colspan");if(r+rs>1000||col+cs>1000||cells.Count>=10000)throw new InvalidDataException("表格结构超出大小上限。");
                for(int y=r;y<r+rs;y++)for(int x=col;x<col+cs;x++)if(!occupied.Add((y,x)))throw new InvalidDataException("表格合并单元格相互重叠，请重试此区域。");
                cells.Add(new(r,col,rs,cs,index<boxes.Count?boxes[index]:null,""));index++;columns=Math.Max(columns,col+cs);col+=cs;
            }
        }
        if(cells.Count==0||boxes.Count!=cells.Count)throw new InvalidDataException("表格结构与单元格坐标数量不匹配。");
        return new(Math.Max(rows.Count,cells.Max(c=>c.Row+c.RowSpan)),columns,cells,score);
    }
    internal static TableResult InDocument(TableResult table,PixelBox region)=>table with{CoordinateSpace="document",Cells=table.Cells.Select(c=>c.Box is{} b?c with{Box=b with{X=b.X+(float)Math.Floor(region.X),Y=b.Y+(float)Math.Floor(region.Y)}}:c).ToList()};
    internal static TableResult Associate(TableResult table,IEnumerable<(string Text,PixelBox Box)> lines)
    {
        var text=table.Cells.Select(_=>new List<(string Text,PixelBox Box)>()).ToArray();
        foreach(var line in lines)
        {
            int index=-1;float overlap=0;double distance=double.MaxValue;
            for(int i=0;i<table.Cells.Count;i++)if(table.Cells[i].Box is{} box)
            {float intersection=box.Intersection(line.Box)/Math.Max(1,line.Box.Area);double d=Math.Pow(box.X+box.Width/2-line.Box.X-line.Box.Width/2,2)+Math.Pow(box.Y+box.Height/2-line.Box.Y-line.Box.Height/2,2);
                if(intersection>overlap||(intersection==overlap&&d<distance)){overlap=intersection;distance=d;index=i;}}
            if(index>=0)text[index].Add(line);
        }
        return table with{Cells=table.Cells.Select((c,i)=>c with{Text=string.Join("\n",text[i].OrderBy(l=>l.Box.Y).ThenBy(l=>l.Box.X).Select(l=>l.Text))}).ToList()};
    }
}
