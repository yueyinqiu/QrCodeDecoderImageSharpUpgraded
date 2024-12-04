Modified from [https://github.com/paperdropio/QRCode](https://github.com/paperdropio/QRCode).

Directly use it with:

```csharp
using QrCodeDecoderImageSharpUpgraded;

var decoder = new QRDecoder();
var result = decoder.ImageDecoder(SixLabors.ImageSharp.Image.Load("pass.png"));
// result is a byte[][]?, each element (byte[]) is a scanned qrcode in the image
// if there are no qrcodes found, it will be null, rather than an empty array
var ifItsAUtf8StringYouCould = Encoding.UTF8.GetString(result[0]);
Console.WriteLine(ifItsAUtf8StringYouCould);
```