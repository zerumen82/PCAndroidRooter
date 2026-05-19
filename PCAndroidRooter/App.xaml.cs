using System;
using System.IO;
using System.Windows;

namespace PCAndroidRooter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CreateIconIfMissing();
        base.OnStartup(e);
    }

    private void CreateIconIfMissing()
    {
        try
        {
            string iconPath = "Resources/android_capsule_icon.ico";
            string directory = Path.GetDirectoryName(iconPath);

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(iconPath))
            {
                CreateSimpleAndroidCapsuleIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create icon: {ex.Message}");
        }
    }

    private void CreateSimpleAndroidCapsuleIcon(string path)
    {
        // Create a very basic 16x16 icon using binary ICO format
        // This creates a simple green circle with white details
        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            // ICONDIR structure (6 bytes)
            bw.Write((ushort)0); // Reserved
            bw.Write((ushort)1); // Type (1 for ICO)
            bw.Write((ushort)1); // Number of images

            // ICONDIRENTRY structure (16 bytes)
            bw.Write((byte)16); // Width
            bw.Write((byte)16); // Height
            bw.Write((byte)0);  // Color palette size (0 = no palette)
            bw.Write((byte)0);  // Reserved
            bw.Write((ushort)0); // Color planes
            bw.Write((ushort)32); // Bits per pixel
            uint imageSize = 0; // Will fill in later
            bw.Write(imageSize); // Size of image data
            uint offset = 22; // Start of image data (after headers)
            bw.Write(offset); // Offset of image data

            // Create simple 16x16 image data (BGRA format, 32-bit)
            byte[] imageData = CreateAndroidCapsuleImageData();
            imageSize = (uint)imageData.Length;

            // Go back and update the image size
            fs.Position = 8;
            bw.Write(imageSize);

            // Write image data
            fs.Position = offset;
            bw.Write(imageData);
        }
    }

    private byte[] CreateAndroidCapsuleImageData()
    {
        // Create a 16x16 BGRA image
        // Simple representation: green background, white android, red capsule
        byte[] pixels = new byte[16 * 16 * 4]; // 4 bytes per pixel (BGRA)

        // Fill with transparent pixels first
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;     // B
            pixels[i + 1] = 0; // G
            pixels[i + 2] = 0; // R
            pixels[i + 3] = 0; // A
        }

        // Draw a simple android-like figure
        // Background circle (Android green)
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int dx = x - 8;
                int dy = y - 8;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance <= 7) // Circle radius
                {
                    int index = (y * 16 + x) * 4;
                    pixels[index] = 132;     // B
                    pixels[index + 1] = 220; // G
                    pixels[index + 2] = 61;  // R
                    pixels[index + 3] = 255; // A
                }
            }
        }

        // Draw simple white android shape (very simplified)
        // Head
        DrawRectangle(pixels, 6, 2, 4, 4, 255, 255, 255, 255);
        // Body
        DrawRectangle(pixels, 6, 6, 4, 6, 255, 255, 255, 255);
        // Eyes
        DrawPixel(pixels, 7, 3, 0, 0, 0, 255); // Android green eyes
        DrawPixel(pixels, 9, 3, 0, 0, 0, 255);
        // Capsule (red)
        DrawRectangle(pixels, 7, 10, 2, 3, 255, 60, 60, 255);

        return pixels;
    }

    private void DrawPixel(byte[] pixels, int x, int y, byte b, byte g, byte r, byte a)
    {
        if (x >= 0 && x < 16 && y >= 0 && y < 16)
        {
            int index = (y * 16 + x) * 4;
            pixels[index] = b;
            pixels[index + 1] = g;
            pixels[index + 2] = r;
            pixels[index + 3] = a;
        }
    }

    private void DrawRectangle(byte[] pixels, int x, int y, int width, int height, byte b, byte g, byte r, byte a)
    {
        for (int py = y; py < y + height && py < 16; py++)
        {
            for (int px = x; px < x + width && px < 16; px++)
            {
                DrawPixel(pixels, px, py, b, g, r, a);
            }
        }
    }
}