using Cursorial.Rendering;

namespace Cursorial.UI.Xaml.Markup;

/// <summary>
/// Common constant values that can be referenced in Xaml by <c>{x:Static}</c>. 
/// </summary>
public static class XamlConstants
{
    public static Boolean True => true;
    public static Boolean False => false;

    public static Int32 Int32Zero => 0;
    public static Int32 Int32One => 1;
    public static Int32 Int32MinusOne => -1;

    public static Int64 Int64Zero => 0L;
    public static Int64 Int64One => 1L;
    public static Int64 Int64MinusOne => -1L;

    public static Double DoubleZero => 0d;
    public static Double DoubleOne => 1d;
    public static Double DoubleMinusOne => -1d;

    public static Single SingleZero => 0f;
    public static Single SingleOne => 1f;
    public static Single SingleMinusOne => -1f;

    public static string StringEmpty => string.Empty;
    public static string StringSpace => " ";
    public static string StringDurableSpace => "\u00A0";

    public static Margins MarginsLeft1 => new(1, 0, 0, 0);
    public static Margins MarginsTop1 => new(0, 1, 0, 0);
    public static Margins MarginsRight1 => new(0, 0, 1, 0);
    public static Margins MarginsBottom1 => new(0, 0, 0, 1);
    public static Margins MarginsLeftRight1 => new(1, 0);
    public static Margins MarginsTopBottom1 => new(0, 1);
    public static Margins Margins2X1 => new(2, 1);

    public static Size Size1X1 =>  new(1, 1);
    public static Size Size2X1 =>  new(2, 1);
    public static Size SizeUnbounded =>  new(LayoutMath.Unbounded, LayoutMath.Unbounded);
    public static Size SizeMaxExtent =>  new(LayoutMath.MaxExtent, LayoutMath.MaxExtent);
}