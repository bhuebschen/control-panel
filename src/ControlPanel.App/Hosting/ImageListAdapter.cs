using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ControlPanel.App.Interop;
using ControlPanel.App.Native;

namespace ControlPanel.App.Hosting;

/// <summary>
/// Our implementation of MMC's IImageList, handed to a snap-in via
/// IConsole.QueryScopeImageList / QueryResultImageList (or via the
/// MMCN_ADD_IMAGES notification's arg). The snap-in calls back into this
/// to register the icons it wants to use for its nodes; we materialize
/// them into a real WinForms ImageList the TreeView/ListView render from.
/// </summary>
[ComVisible(true)]
internal sealed class ImageListAdapter : IImageList
{
    private readonly ImageList _target;

    public ImageListAdapter(ImageList target)
    {
        _target = target;
    }

    public void ImageListSetIcon(IntPtr pIcon, int nLoc)
    {
        // Despite the "LONG_PTR*" typing in the IDL, real snap-ins pass the
        // HICON value itself reinterpret-cast to that pointer type (e.g.
        // "ImageListSetIcon((LONG_PTR*)hIcon, nLoc)"), not the address of a
        // variable holding it - confirmed against the Microsoft Learn
        // parameter docs and matching sample code, and the hard way, via an
        // AccessViolationException from treating it as a real indirection.
        if (pIcon == IntPtr.Zero)
        {
            return;
        }

        try
        {
            using var icon = Icon.FromHandle(pIcon);
            SetImage(nLoc, icon.ToBitmap());
        }
        finally
        {
            Win32.DestroyIcon(pIcon);
        }
    }

    public void ImageListSetStrip(IntPtr pBMapSm, IntPtr pBMapLg, int nStartLoc, int cMask)
    {
        // We only maintain a single (small, 16x16-class) image list, so the
        // large-icon strip is intentionally ignored here. As with
        // ImageListSetIcon, pBMapSm/pBMapLg are HBITMAP values themselves,
        // not addresses of variables holding them.
        if (pBMapSm == IntPtr.Zero)
        {
            return;
        }

        try
        {
            using var stripBitmap = Image.FromHbitmap(pBMapSm);
            int cellWidth = _target.ImageSize.Width;
            int cellHeight = _target.ImageSize.Height;
            int count = stripBitmap.Width / Math.Max(cellWidth, 1);
            Color transparent = ColorTranslator.FromWin32(cMask);

            for (int i = 0; i < count; i++)
            {
                var cell = new Bitmap(cellWidth, cellHeight);
                using (var g = Graphics.FromImage(cell))
                {
                    g.DrawImage(
                        stripBitmap,
                        new Rectangle(0, 0, cellWidth, cellHeight),
                        new Rectangle(i * cellWidth, 0, cellWidth, cellHeight),
                        GraphicsUnit.Pixel);
                }

                cell.MakeTransparent(transparent);
                SetImage(nStartLoc + i, cell);
            }
        }
        finally
        {
            Win32.DeleteObject(pBMapSm);
            if (pBMapLg != IntPtr.Zero)
            {
                Win32.DeleteObject(pBMapLg);
            }
        }
    }

    private void SetImage(int index, Image image)
    {
        if (index < 0)
        {
            return;
        }

        while (_target.Images.Count <= index)
        {
            _target.Images.Add(new Bitmap(_target.ImageSize.Width, _target.ImageSize.Height));
        }

        _target.Images[index] = image;
    }
}
