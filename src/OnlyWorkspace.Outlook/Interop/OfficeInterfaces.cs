using System;
using System.Runtime.InteropServices;

namespace OnlyWorkspace.Outlook.Interop;

[ComImport]
[ComVisible(true)]
[Guid("000C0396-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IRibbonExtensibility
{
    [DispId(1)]
    string GetCustomUI(string RibbonID);
}

[ComImport]
[ComVisible(true)]
[Guid("000C0395-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IRibbonControl
{
    [DispId(1)]
    string Id { get; }
    [DispId(2)]
    object Context { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(3)]
    string Tag { get; }
}

[ComImport]
[ComVisible(true)]
[Guid("000C033E-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface ICustomTaskPaneConsumer
{
    [DispId(1)]
    void CTPFactoryAvailable([MarshalAs(UnmanagedType.Interface)] ICTPFactory CTPFactoryInst);
}

[ComImport]
[ComVisible(true)]
[Guid("000C033D-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface ICTPFactory
{
    [DispId(1)]
    [return: MarshalAs(UnmanagedType.Interface)]
    _CustomTaskPane CreateCTP([MarshalAs(UnmanagedType.BStr)] string CTPAxID, [MarshalAs(UnmanagedType.BStr)] string CTPTitle, [MarshalAs(UnmanagedType.Struct)] object? CTPParentWindow);
}

[ComImport]
[ComVisible(true)]
[Guid("000C033B-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface _CustomTaskPane
{
    [DispId(0)]
    string Title { get; }

    [DispId(1)]
    object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    [DispId(2)]
    object Window { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    [DispId(3)]
    bool Visible { get; set; }

    [DispId(4)]
    object ContentControl { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    [DispId(5)]
    int Height { get; set; }

    [DispId(6)]
    int Width { get; set; }

    [DispId(7)]
    MsoCTPDockPosition DockPosition { get; set; }

    [DispId(8)]
    MsoCTPDockPositionRestrict DockPositionRestrict { get; set; }

    [DispId(9)]
    void Delete();
}

public enum MsoCTPDockPosition
{
    msoCTPDockPositionLeft = 0,
    msoCTPDockPositionTop = 1,
    msoCTPDockPositionRight = 2,
    msoCTPDockPositionBottom = 3,
    msoCTPDockPositionFloating = 4
}

public enum MsoCTPDockPositionRestrict
{
    msoCTPDockPositionRestrictNone = 0,
    msoCTPDockPositionRestrictNoChange = 1,
    msoCTPDockPositionRestrictNoHorizontal = 2,
    msoCTPDockPositionRestrictNoVertical = 3
}

