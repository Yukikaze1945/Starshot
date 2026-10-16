using Starshot.Features.Workspace;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// Real WIC PNG roundtrip, entirely in CPU memory. No Win2D, model, WGC or files.
int checks=0;void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
byte[][] colors=[[255,0,0,255],[0,255,0,255],[0,0,255,255],[245,244,235,255],[221,243,105,255],[40,45,34,255]];
const int width=96,height=8;
byte[] rgba=new byte[width*height*4];
for(int y=0;y<height;y++)for(int x=0;x<width;x++)colors[x/16].CopyTo(rgba,(y*width+x)*4);
foreach(var order in new[]{WorkspacePixels.ByteOrder.Rgba8,WorkspacePixels.ByteOrder.Bgra8})
{
    byte[] input=(byte[])rgba.Clone();
    if(order==WorkspacePixels.ByteOrder.Bgra8)for(int i=0;i<input.Length;i+=4)(input[i],input[i+2])=(input[i+2],input[i]);
    byte[] untouched=(byte[])input.Clone(),bgra=new byte[input.Length];
    WorkspacePixels.CopyBgra8(input,bgra,order);
    using var stream=new InMemoryRandomAccessStream();
    var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
    encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,width,height,96,96,bgra);
    await encoder.FlushAsync();stream.Seek(0);
    var decoder=await BitmapDecoder.CreateAsync(stream);
    var data=await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8,BitmapAlphaMode.Premultiplied,new BitmapTransform(),ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage);
    byte[] decoded=data.DetachPixelData();
    Check(decoded.SequenceEqual(rgba),$"{order}: PNG displays the original red/green/blue, warm paper and accent");
    Check(input.SequenceEqual(untouched),$"{order}: encoding must not modify source");
    Check(decoder.PixelWidth==width&&decoder.PixelHeight==height,"Geometry unchanged");
}
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{ok=true,checks,cpuOnly=true,filesWritten=0,colors=new[]{"red","green","blue","warm paper","Starshot accent","dark neutral"}}));
