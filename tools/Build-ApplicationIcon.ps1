[CmdletBinding()]
param(
    [string]$Source,
    [string]$PngOutput,
    [string]$IcoOutput
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $repositoryRoot 'assets\branding\1SalemServerManager-Icon-Source.png'
}
if ([string]::IsNullOrWhiteSpace($PngOutput)) {
    $PngOutput = Join-Path $repositoryRoot 'assets\branding\1SalemServerManager-Icon.png'
}
if ([string]::IsNullOrWhiteSpace($IcoOutput)) {
    $IcoOutput = Join-Path $repositoryRoot 'assets\branding\1SalemServerManager.ico'
}

Add-Type -AssemblyName System.Drawing

$typeDefinition = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class OneSalemIconBuilder
{
    private static readonly int[] RequiredSizes =
        { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Build(string sourcePath, string pngPath, string icoPath)
    {
        using (var source = new Bitmap(sourcePath))
        using (var transparent = RemoveConnectedBlackBackground(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
            transparent.Save(pngPath, ImageFormat.Png);

            var images = new List<byte[]>();
            foreach (var size in RequiredSizes)
            {
                using (var resized = new Bitmap(
                    size,
                    size,
                    PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(resized))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.DrawImage(
                            transparent,
                            new Rectangle(0, 0, size, size),
                            0,
                            0,
                            transparent.Width,
                            transparent.Height,
                            GraphicsUnit.Pixel);
                    }

                    using (var memory = new MemoryStream())
                    {
                        resized.Save(memory, ImageFormat.Png);
                        images.Add(memory.ToArray());
                    }
                }
            }

            WriteIco(icoPath, images);
        }
    }

    private static Bitmap RemoveConnectedBlackBackground(Bitmap source)
    {
        var bitmap = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var visited = new bool[bitmap.Width * bitmap.Height];
            var queue = new Queue<int>();

            for (var x = 0; x < bitmap.Width; x++)
            {
                EnqueueIfBackground(
                    x, 0, bitmap.Width, bitmap.Height, data.Stride,
                    bytes, visited, queue);
                EnqueueIfBackground(
                    x, bitmap.Height - 1, bitmap.Width, bitmap.Height,
                    data.Stride, bytes, visited, queue);
            }

            for (var y = 1; y < bitmap.Height - 1; y++)
            {
                EnqueueIfBackground(
                    0, y, bitmap.Width, bitmap.Height, data.Stride,
                    bytes, visited, queue);
                EnqueueIfBackground(
                    bitmap.Width - 1, y, bitmap.Width, bitmap.Height,
                    data.Stride, bytes, visited, queue);
            }

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % bitmap.Width;
                var y = index / bitmap.Width;
                var offset = (y * data.Stride) + (x * 4);
                bytes[offset + 3] = 0;
                EnqueueIfBackground(
                    x - 1, y, bitmap.Width, bitmap.Height, data.Stride,
                    bytes, visited, queue);
                EnqueueIfBackground(
                    x + 1, y, bitmap.Width, bitmap.Height, data.Stride,
                    bytes, visited, queue);
                EnqueueIfBackground(
                    x, y - 1, bitmap.Width, bitmap.Height, data.Stride,
                    bytes, visited, queue);
                EnqueueIfBackground(
                    x, y + 1, bitmap.Width, bitmap.Height, data.Stride,
                    bytes, visited, queue);
            }

            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static void EnqueueIfBackground(
        int x,
        int y,
        int width,
        int height,
        int stride,
        byte[] bytes,
        bool[] visited,
        Queue<int> queue)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = (y * width) + x;
        if (visited[index])
        {
            return;
        }

        var offset = (y * stride) + (x * 4);
        var blue = bytes[offset];
        var green = bytes[offset + 1];
        var red = bytes[offset + 2];
        if (red <= 2 && green <= 2 && blue <= 2)
        {
            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static void WriteIco(string path, IList<byte[]> images)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)images.Count);

            var offset = 6 + (16 * images.Count);
            for (var index = 0; index < images.Count; index++)
            {
                var size = RequiredSizes[index];
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[index].Length);
                writer.Write(offset);
                offset += images[index].Length;
            }

            foreach (var image in images)
            {
                writer.Write(image);
            }
        }
    }
}
'@

Add-Type -TypeDefinition $typeDefinition -ReferencedAssemblies System.Drawing

$sourcePath = [System.IO.Path]::GetFullPath($Source)
$pngPath = [System.IO.Path]::GetFullPath($PngOutput)
$icoPath = [System.IO.Path]::GetFullPath($IcoOutput)
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Icon source not found: $sourcePath"
}

[OneSalemIconBuilder]::Build($sourcePath, $pngPath, $icoPath)
Write-Host "Official PNG: $pngPath"
Write-Host "Windows ICO: $icoPath"
