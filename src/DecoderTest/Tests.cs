using NUnit.Framework;
using QrCodeDecoderImageSharpUpgraded;
using System.Text;

namespace DecoderTest;

public class Tests
{
    [Test]
    public void Test()
    {
        QRDecoder decoder = new();

        var result = decoder.ImageDecoder(SixLabors.ImageSharp.Image.Load("pass.png"));
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Length == 1);
        var decodedResult = Encoding.UTF8.GetString(result[0]);
        Assert.AreEqual(decodedResult, "Bugs Bunny\n07/27/1940");

        result = decoder.ImageDecoder(SixLabors.ImageSharp.Image.Load("fail.jpg"));
        Assert.IsNull(result);
    }
}