using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Starshot.Features.DocumentOcr;

internal static class DocumentExport
{
    internal static string Markdown(PageRecognitionResult page)=>string.Join("\n\n",page.Regions.Where(r=>r.ParentId is null).OrderBy(r=>r.Order).Select(r=>
        r.State!="done"?$"<!-- {r.Label}: {r.Error??r.State} -->":r.Formula is{} f?$"$$\n{f.Latex}\n$$":r.Table is{} t?t.Html():r.IsImage?$"<!-- 图片区域 {r.Order}：{r.Box.X:0},{r.Box.Y:0},{r.Box.Width:0},{r.Box.Height:0} -->":r.Label is "doc_title" or "paragraph_title"?"## "+r.Text:r.Text));
    internal static void Excel(string path,TableResult table)
    {
        using var document=SpreadsheetDocument.Create(path,SpreadsheetDocumentType.Workbook);
        var workbook=document.AddWorkbookPart();workbook.Workbook=new Workbook();var sheet=workbook.AddNewPart<WorksheetPart>();var data=new SheetData();var merges=new MergeCells();
        string Column(int i){string value="";do{value=(char)('A'+i%26)+value;i=i/26-1;}while(i>=0);return value;}
        for(int r=0;r<table.Rows;r++)
        {
            var row=new Row{RowIndex=(uint)r+1};foreach(var c in table.Cells.Where(c=>c.Row==r).OrderBy(c=>c.Column))
            {
                string address=Column(c.Column)+(r+1);row.Append(new Cell{CellReference=address,DataType=CellValues.InlineString,InlineString=new InlineString(new Text(c.Text){Space=SpaceProcessingModeValues.Preserve})});
                if(c.RowSpan>1||c.ColSpan>1)merges.Append(new MergeCell{Reference=address+":"+Column(c.Column+c.ColSpan-1)+(r+c.RowSpan)});
            }
            data.Append(row);
        }
        sheet.Worksheet=new Worksheet(data);if(merges.HasChildren)sheet.Worksheet.Append(merges);
        workbook.Workbook.Append(new Sheets(new Sheet{Id=workbook.GetIdOfPart(sheet),SheetId=1,Name="识别表格"}));workbook.Workbook.Save();
    }
}
