using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Fyltrix.UI;

/// <summary>Dark or light theme. Colours live here; control templates are small XAML snippets loaded at startup (so the theme is
/// chosen once, when Fyltrix starts: <see cref="Use"/> before <see cref="Apply"/>).</summary>
public static class Theme
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Bg0"] = "#141417",     // window / tab strip
        ["Bg1"] = "#1B1B1F",     // content
        ["Bg2"] = "#232329",     // bars, headers
        ["Bg3"] = "#2C2C34",     // inputs, hover
        ["Hover"] = "#30303A",
        ["Sel"] = "#264F78",     // selection (focused)
        ["SelInactive"] = "#33415A",
        ["Line"] = "#34343E",
        ["Fg1"] = "#E6E6EA",
        ["Fg2"] = "#A0A0AA",
        ["Fg3"] = "#6E6E7A",
        ["Accent"] = "#5AA9FF",
        ["Danger"] = "#FF8A80",
        ["Star"] = "#F5C451",
        ["Ok"] = "#6FD08C",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Bg0"] = "#EEEEF1",     // window / tab strip
        ["Bg1"] = "#FFFFFF",     // content
        ["Bg2"] = "#F6F6F8",     // bars, headers
        ["Bg3"] = "#EAEAEE",     // inputs, hover
        ["Hover"] = "#E4E4EA",
        ["Sel"] = "#CCE3FB",     // selection (focused)
        ["SelInactive"] = "#E0E6EE",
        ["Line"] = "#D9D9DF",
        ["Fg1"] = "#1B1B1F",
        ["Fg2"] = "#55555F",
        ["Fg3"] = "#85858F",
        ["Accent"] = "#0A64C2",
        ["Danger"] = "#C42B1C",
        ["Star"] = "#A86E00",
        ["Ok"] = "#107C10",
    };

    public static Dictionary<string, string> Colors { get; private set; } = Dark;
    public static bool IsLight { get; private set; }

    /// <summary>Chooses the palette (before <see cref="Apply"/>; Fyltrix does it once at startup).</summary>
    public static void Use(Core.ThemeMode mode)
    {
        IsLight = mode == Core.ThemeMode.Light || (mode == Core.ThemeMode.System && WindowsUsesLightTheme());
        Colors = IsLight ? Light : Dark;
        Brushes.Clear();
    }

    /// <summary>Windows' "app mode" (Settings → Personalisation → Colours).</summary>
    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>The content background as a System.Drawing colour (WebView2 and WinForms hosts paint it before content loads).</summary>
    public static System.Drawing.Color DrawingColor(string key)
    {
        var c = (Color)ColorConverter.ConvertFromString(Colors[key]);
        return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
    }

    public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static SolidColorBrush Brush(string key)
    {
        if (Brushes.TryGetValue(key, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors[key]));
        b.Freeze();
        Brushes[key] = b;
        return b;
    }

    private static string C(string key) => Colors[key];

    /// <summary>XAML snippets that failed to load (the control then falls back to the default look).</summary>
    public static List<string> SnippetErrors { get; } = new();

    public static void Apply(Application app)
    {
        foreach (var kv in Colors) app.Resources[kv.Key + "Brush"] = Brush(kv.Key);
        app.Resources[SystemColors.HighlightBrushKey] = Brush("Sel");
        app.Resources[SystemColors.ControlBrushKey] = Brush("Bg2");
        app.Resources[SystemColors.WindowBrushKey] = Brush("Bg1");
        app.Resources[SystemColors.ControlTextBrushKey] = Brush("Fg1");
        app.Resources[SystemColors.WindowTextBrushKey] = Brush("Fg1");
        app.Resources[SystemColors.MenuBrushKey] = Brush("Bg2");
        app.Resources[SystemColors.MenuTextBrushKey] = Brush("Fg1");

        foreach (var snippet in Snippets())
        {
            try
            {
                var xaml = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                           "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + snippet + "</ResourceDictionary>";
                var dict = (ResourceDictionary)XamlReader.Parse(xaml);
                app.Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                SnippetErrors.Add(ex.Message);
                System.Diagnostics.Debug.WriteLine("Theme snippet failed: " + ex.Message);
            }
        }
    }

    private static IEnumerable<string> Snippets()
    {
        // Buttons: flat, rounded
        yield return $@"
<Style TargetType=""Button"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Background"" Value=""Transparent""/>
  <Setter Property=""BorderBrush"" Value=""Transparent""/>
  <Setter Property=""BorderThickness"" Value=""1""/>
  <Setter Property=""Padding"" Value=""8,4""/>
  <Setter Property=""Cursor"" Value=""Hand""/>
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""Button"">
        <Border x:Name=""Bd"" Background=""{{TemplateBinding Background}}"" BorderBrush=""{{TemplateBinding BorderBrush}}""
                BorderThickness=""{{TemplateBinding BorderThickness}}"" CornerRadius=""4"" Padding=""{{TemplateBinding Padding}}"">
          <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
          <Trigger Property=""IsPressed"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Sel")}""/></Trigger>
          <Trigger Property=""IsEnabled"" Value=""False""><Setter Property=""Opacity"" Value=""0.4""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Text boxes
        yield return $@"
<Style TargetType=""TextBox"">
  <Setter Property=""Background"" Value=""{C("Bg3")}""/>
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""BorderBrush"" Value=""{C("Line")}""/>
  <Setter Property=""CaretBrush"" Value=""{C("Fg1")}""/>
  <Setter Property=""SelectionBrush"" Value=""{C("Accent")}""/>
  <Setter Property=""Padding"" Value=""6,3""/>
  <Setter Property=""VerticalContentAlignment"" Value=""Center""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""TextBox"">
        <Border x:Name=""Bd"" Background=""{{TemplateBinding Background}}"" BorderBrush=""{{TemplateBinding BorderBrush}}""
                BorderThickness=""1"" CornerRadius=""4"">
          <ScrollViewer x:Name=""PART_ContentHost"" Margin=""{{TemplateBinding Padding}}"" VerticalAlignment=""{{TemplateBinding VerticalContentAlignment}}""
                        Focusable=""False"" HorizontalScrollBarVisibility=""Hidden"" VerticalScrollBarVisibility=""Hidden""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Fg3")}""/></Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Accent")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // List view rows
        yield return $@"
<Style TargetType=""ListViewItem"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Background"" Value=""Transparent""/>
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""HorizontalContentAlignment"" Value=""Stretch""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ListViewItem"">
        <!-- the row ends at the last column (as in Explorer): the highlight stops there and the space to the right is background -->
        <Border x:Name=""Bd"" HorizontalAlignment=""Left"" Background=""{{TemplateBinding Background}}"" BorderThickness=""1"" BorderBrush=""Transparent"" CornerRadius=""3"" Padding=""0,2"" SnapsToDevicePixels=""True"">
          <GridViewRowPresenter VerticalAlignment=""Center"" Columns=""{{TemplateBinding GridView.ColumnCollection}}"" Content=""{{TemplateBinding Content}}""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
          <Trigger Property=""IsSelected"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Sel")}""/></Trigger>
          <MultiTrigger>
            <MultiTrigger.Conditions>
              <Condition Property=""IsSelected"" Value=""True""/>
              <Condition Property=""Selector.IsSelectionActive"" Value=""False""/>
            </MultiTrigger.Conditions>
            <Setter TargetName=""Bd"" Property=""Background"" Value=""{C("SelInactive")}""/>
          </MultiTrigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Fg3")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Column headers
        yield return $@"
<Style TargetType=""GridViewColumnHeader"">
  <Setter Property=""Foreground"" Value=""{C("Fg2")}""/>
  <Setter Property=""Background"" Value=""{C("Bg1")}""/>
  <Setter Property=""HorizontalContentAlignment"" Value=""Left""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""GridViewColumnHeader"">
        <Grid>
          <Border x:Name=""Bd"" Background=""{{TemplateBinding Background}}"" BorderBrush=""{C("Line")}"" BorderThickness=""0,0,1,1"" Padding=""8,5"">
            <ContentPresenter HorizontalAlignment=""{{TemplateBinding HorizontalContentAlignment}}"" VerticalAlignment=""Center""/>
          </Border>
          <Thumb x:Name=""PART_HeaderGripper"" HorizontalAlignment=""Right"" Width=""6"" Margin=""0,0,-3,0"" Cursor=""SizeWE"">
            <Thumb.Template><ControlTemplate TargetType=""Thumb""><Border Background=""Transparent""/></ControlTemplate></Thumb.Template>
          </Thumb>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Bg3")}""/></Trigger>
          <Trigger Property=""Role"" Value=""Padding""><Setter TargetName=""Bd"" Property=""BorderThickness"" Value=""0,0,0,1""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Thin dark scrollbars
        yield return $@"
<Style x:Key=""FlScrollThumb"" TargetType=""Thumb"">
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""Thumb"">
        <Border x:Name=""T"" Background=""#4A4A55"" CornerRadius=""4""/>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""T"" Property=""Background"" Value=""#6A6A77""/></Trigger>
          <Trigger Property=""IsDragging"" Value=""True""><Setter TargetName=""T"" Property=""Background"" Value=""#8A8A97""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style x:Key=""FlScrollPage"" TargetType=""RepeatButton"">
  <Setter Property=""Focusable"" Value=""False""/>
  <Setter Property=""Template"">
    <Setter.Value><ControlTemplate TargetType=""RepeatButton""><Border Background=""Transparent""/></ControlTemplate></Setter.Value>
  </Setter>
</Style>
<Style TargetType=""ScrollBar"">
  <Setter Property=""Background"" Value=""Transparent""/>
  <Setter Property=""Width"" Value=""10""/>
  <Setter Property=""MinWidth"" Value=""10""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ScrollBar"">
        <Border Background=""{{TemplateBinding Background}}"" Padding=""2"">
          <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
            <Track.DecreaseRepeatButton><RepeatButton Style=""{{StaticResource FlScrollPage}}"" Command=""ScrollBar.PageUpCommand""/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Style=""{{StaticResource FlScrollPage}}"" Command=""ScrollBar.PageDownCommand""/></Track.IncreaseRepeatButton>
            <Track.Thumb><Thumb Style=""{{StaticResource FlScrollThumb}}""/></Track.Thumb>
          </Track>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
  <Style.Triggers>
    <Trigger Property=""Orientation"" Value=""Horizontal"">
      <Setter Property=""Width"" Value=""Auto""/>
      <Setter Property=""MinWidth"" Value=""0""/>
      <Setter Property=""Height"" Value=""10""/>
      <Setter Property=""MinHeight"" Value=""10""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""ScrollBar"">
            <Border Background=""{{TemplateBinding Background}}"" Padding=""2"">
              <Track x:Name=""PART_Track"" IsDirectionReversed=""False"">
                <Track.DecreaseRepeatButton><RepeatButton Style=""{{StaticResource FlScrollPage}}"" Command=""ScrollBar.PageLeftCommand""/></Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton><RepeatButton Style=""{{StaticResource FlScrollPage}}"" Command=""ScrollBar.PageRightCommand""/></Track.IncreaseRepeatButton>
                <Track.Thumb><Thumb Style=""{{StaticResource FlScrollThumb}}""/></Track.Thumb>
              </Track>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Trigger>
  </Style.Triggers>
</Style>";

        // Context menus (flat, no submenus)
        yield return $@"
<Style TargetType=""ContextMenu"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ContextMenu"">
        <Border Background=""{C("Bg2")}"" BorderBrush=""{C("Line")}"" BorderThickness=""1"" CornerRadius=""6"" Padding=""4"">
          <StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Cycle""/>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style TargetType=""MenuItem"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""MenuItem"">
        <Border x:Name=""Bd"" Background=""Transparent"" CornerRadius=""4"" Padding=""8,5"" MinWidth=""220"">
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width=""26""/>
              <ColumnDefinition Width=""*""/>
              <ColumnDefinition Width=""Auto""/>
              <ColumnDefinition Width=""14""/>
            </Grid.ColumnDefinitions>
            <ContentPresenter Grid.Column=""0"" ContentSource=""Icon"" VerticalAlignment=""Center"" HorizontalAlignment=""Left""/>
            <ContentPresenter Grid.Column=""1"" ContentSource=""Header"" RecognizesAccessKey=""True"" VerticalAlignment=""Center""/>
            <TextBlock Grid.Column=""2"" Text=""{{TemplateBinding InputGestureText}}"" Foreground=""{C("Fg3")}"" Margin=""24,0,0,0"" VerticalAlignment=""Center""/>
            <Path x:Name=""Arrow"" Grid.Column=""3"" Data=""M0,0 L4,4 L0,8"" Stroke=""{C("Fg2")}"" StrokeThickness=""1.4"" HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Visibility=""Collapsed""/>
            <Popup x:Name=""PART_Popup"" Placement=""Right"" HorizontalOffset=""10"" VerticalOffset=""-9"" IsOpen=""{{TemplateBinding IsSubmenuOpen}}""
                   AllowsTransparency=""True"" Focusable=""False"" PopupAnimation=""None"">
              <Border Background=""{C("Bg2")}"" BorderBrush=""{C("Line")}"" BorderThickness=""1"" CornerRadius=""6"" Padding=""4"">
                <ScrollViewer VerticalScrollBarVisibility=""Auto"" MaxHeight=""640"">
                  <StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Cycle"" Grid.IsSharedSizeScope=""True""/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""Role"" Value=""SubmenuHeader""><Setter TargetName=""Arrow"" Property=""Visibility"" Value=""Visible""/></Trigger>
          <Trigger Property=""IsHighlighted"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
          <Trigger Property=""IsSubmenuOpen"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
          <Trigger Property=""IsEnabled"" Value=""False""><Setter Property=""Opacity"" Value=""0.4""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style x:Key=""{{x:Static MenuItem.SeparatorStyleKey}}"" TargetType=""Separator"">
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""Separator""><Border Height=""1"" Background=""{C("Line")}"" Margin=""6,4""/></ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Tooltips
        yield return $@"
<Style TargetType=""ToolTip"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Background"" Value=""{C("Bg3")}""/>
  <Setter Property=""BorderBrush"" Value=""{C("Line")}""/>
  <Setter Property=""Padding"" Value=""8,5""/>
</Style>";

        // Combo boxes (non-editable)
        yield return $@"
<Style x:Key=""FlComboToggle"" TargetType=""ToggleButton"">
  <Setter Property=""Focusable"" Value=""False""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ToggleButton"">
        <Border x:Name=""Bd"" Background=""{C("Bg3")}"" BorderBrush=""{C("Line")}"" BorderThickness=""1"" CornerRadius=""4"">
          <Path HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,9,0"" Data=""M0,0 L4,4 L8,0"" Stroke=""{C("Fg2")}"" StrokeThickness=""1.4""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Fg3")}""/></Trigger>
          <Trigger Property=""IsChecked"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Accent")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style TargetType=""ComboBox"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Padding"" Value=""8,4,28,4""/>
  <Setter Property=""MinHeight"" Value=""28""/>
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ComboBox"">
        <Grid>
          <ToggleButton Style=""{{StaticResource FlComboToggle}}"" ClickMode=""Press""
                        IsChecked=""{{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={{RelativeSource TemplatedParent}}}}""/>
          <ContentPresenter IsHitTestVisible=""False"" Margin=""{{TemplateBinding Padding}}"" VerticalAlignment=""Center"" HorizontalAlignment=""Left""
                            Content=""{{TemplateBinding SelectionBoxItem}}"" ContentTemplate=""{{TemplateBinding SelectionBoxItemTemplate}}""/>
          <Popup x:Name=""PART_Popup"" Placement=""Bottom"" IsOpen=""{{TemplateBinding IsDropDownOpen}}"" AllowsTransparency=""True"" Focusable=""False"">
            <Border Background=""{C("Bg2")}"" BorderBrush=""{C("Line")}"" BorderThickness=""1"" CornerRadius=""4"" Padding=""3""
                    MinWidth=""{{Binding ActualWidth, RelativeSource={{RelativeSource TemplatedParent}}}}"" MaxHeight=""{{TemplateBinding MaxDropDownHeight}}"">
              <ScrollViewer VerticalScrollBarVisibility=""Auto""><StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Contained""/></ScrollViewer>
            </Border>
          </Popup>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsEnabled"" Value=""False""><Setter Property=""Opacity"" Value=""0.45""/></Trigger>
          <Trigger Property=""IsKeyboardFocusWithin"" Value=""True""><Setter Property=""Foreground"" Value=""{C("Fg1")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style TargetType=""ComboBoxItem"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""Padding"" Value=""8,5""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ComboBoxItem"">
        <Border x:Name=""Bd"" Background=""Transparent"" CornerRadius=""3"" Padding=""{{TemplateBinding Padding}}""><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsHighlighted"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
          <Trigger Property=""IsSelected"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Sel")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Check boxes and radio buttons
        yield return $@"
<Style TargetType=""CheckBox"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""CheckBox"">
        <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
          <Border x:Name=""Box"" Width=""16"" Height=""16"" CornerRadius=""3"" Background=""{C("Bg3")}"" BorderBrush=""{C("Fg3")}"" BorderThickness=""1"" VerticalAlignment=""Center"">
            <Path x:Name=""Mark"" Data=""M3,8 L6.5,11.5 L13,4.5"" Stroke=""White"" StrokeThickness=""1.8"" Visibility=""Collapsed""/>
          </Border>
          <ContentPresenter Margin=""8,0,0,0"" VerticalAlignment=""Center""/>
        </StackPanel>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsChecked"" Value=""True"">
            <Setter TargetName=""Mark"" Property=""Visibility"" Value=""Visible""/>
            <Setter TargetName=""Box"" Property=""Background"" Value=""{C("Sel")}""/>
            <Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{C("Accent")}""/>
          </Trigger>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{C("Accent")}""/></Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{C("Accent")}""/></Trigger>
          <Trigger Property=""IsEnabled"" Value=""False""><Setter Property=""Opacity"" Value=""0.45""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style TargetType=""RadioButton"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""RadioButton"">
        <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
          <Grid Width=""16"" Height=""16"" VerticalAlignment=""Center"">
            <Ellipse x:Name=""Ring"" Fill=""{C("Bg3")}"" Stroke=""{C("Fg3")}"" StrokeThickness=""1""/>
            <Ellipse x:Name=""Dot"" Width=""8"" Height=""8"" Fill=""{C("Accent")}"" Visibility=""Collapsed""/>
          </Grid>
          <ContentPresenter Margin=""8,0,0,0"" VerticalAlignment=""Center""/>
        </StackPanel>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsChecked"" Value=""True""><Setter TargetName=""Dot"" Property=""Visibility"" Value=""Visible""/><Setter TargetName=""Ring"" Property=""Stroke"" Value=""{C("Accent")}""/></Trigger>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Ring"" Property=""Stroke"" Value=""{C("Accent")}""/></Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""Ring"" Property=""Stroke"" Value=""{C("Accent")}""/></Trigger>
          <Trigger Property=""IsEnabled"" Value=""False""><Setter Property=""Opacity"" Value=""0.45""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Progress bars (determinate fill, pulsing when indeterminate)
        yield return $@"
<Style TargetType=""ProgressBar"">
  <Setter Property=""Height"" Value=""6""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ProgressBar"">
        <Grid>
          <Border x:Name=""PART_Track"" Background=""{C("Bg3")}"" CornerRadius=""3""/>
          <Border x:Name=""PART_Indicator"" Background=""{C("Accent")}"" CornerRadius=""3"" HorizontalAlignment=""Left""/>
          <Border x:Name=""Pulse"" Background=""{C("Accent")}"" CornerRadius=""3"" Opacity=""0"" Visibility=""Collapsed""/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsIndeterminate"" Value=""True"">
            <Setter TargetName=""PART_Indicator"" Property=""Visibility"" Value=""Collapsed""/>
            <Setter TargetName=""Pulse"" Property=""Visibility"" Value=""Visible""/>
            <Trigger.EnterActions>
              <BeginStoryboard x:Name=""PulseSb"">
                <Storyboard RepeatBehavior=""Forever"" AutoReverse=""True"">
                  <DoubleAnimation Storyboard.TargetName=""Pulse"" Storyboard.TargetProperty=""Opacity"" From=""0.15"" To=""0.85"" Duration=""0:0:0.8""/>
                </Storyboard>
              </BeginStoryboard>
            </Trigger.EnterActions>
            <Trigger.ExitActions><StopStoryboard BeginStoryboardName=""PulseSb""/></Trigger.ExitActions>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Sliders (seek / volume)
        yield return $@"
<Style x:Key=""FlSliderDecrease"" TargetType=""RepeatButton"">
  <Setter Property=""Focusable"" Value=""False""/>
  <Setter Property=""Template""><Setter.Value><ControlTemplate TargetType=""RepeatButton""><Border Height=""4"" CornerRadius=""2"" Background=""{C("Accent")}""/></ControlTemplate></Setter.Value></Setter>
</Style>
<Style x:Key=""FlSliderIncrease"" TargetType=""RepeatButton"">
  <Setter Property=""Focusable"" Value=""False""/>
  <Setter Property=""Template""><Setter.Value><ControlTemplate TargetType=""RepeatButton""><Border Height=""4"" CornerRadius=""2"" Background=""{C("Bg3")}""/></ControlTemplate></Setter.Value></Setter>
</Style>
<Style TargetType=""Slider"">
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""IsMoveToPointEnabled"" Value=""True""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""Slider"">
        <Grid Background=""Transparent"" MinHeight=""18"">
          <Track x:Name=""PART_Track"" VerticalAlignment=""Center"">
            <Track.DecreaseRepeatButton><RepeatButton Style=""{{StaticResource FlSliderDecrease}}"" Command=""Slider.DecreaseLarge""/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Style=""{{StaticResource FlSliderIncrease}}"" Command=""Slider.IncreaseLarge""/></Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template><ControlTemplate TargetType=""Thumb""><Ellipse x:Name=""E"" Width=""12"" Height=""12"" Fill=""{C("Fg1")}""/>
                  <ControlTemplate.Triggers><Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""E"" Property=""Fill"" Value=""{C("Accent")}""/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // Tabs (tool windows)
        yield return $@"
<Style TargetType=""TabControl"">
  <Setter Property=""Background"" Value=""{C("Bg1")}""/>
  <Setter Property=""BorderThickness"" Value=""0""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""TabControl"">
        <DockPanel>
          <Border DockPanel.Dock=""Top"" BorderBrush=""{C("Line")}"" BorderThickness=""0,0,0,1"">
            <StackPanel IsItemsHost=""True"" Orientation=""Horizontal""/>
          </Border>
          <ContentPresenter ContentSource=""SelectedContent"" Margin=""0,6,0,0""/>
        </DockPanel>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style TargetType=""TabItem"">
  <Setter Property=""Foreground"" Value=""{C("Fg2")}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""TabItem"">
        <Border x:Name=""Bd"" Padding=""14,6"" Margin=""0,0,2,0"" BorderThickness=""0,0,0,2"" BorderBrush=""Transparent"" Background=""Transparent"" Cursor=""Hand"">
          <ContentPresenter ContentSource=""Header""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsSelected"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Accent")}""/>
            <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
          </Trigger>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // List boxes (used by tool windows)
        yield return $@"
<Style TargetType=""ListBox"">
  <Setter Property=""Background"" Value=""{C("Bg1")}""/>
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""BorderBrush"" Value=""{C("Line")}""/>
  <Setter Property=""BorderThickness"" Value=""1""/>
</Style>
<Style TargetType=""ListBoxItem"">
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
  <Setter Property=""HorizontalContentAlignment"" Value=""Stretch""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ListBoxItem"">
        <Border x:Name=""Bd"" Background=""Transparent"" CornerRadius=""3"" Padding=""6,4"" BorderThickness=""1"" BorderBrush=""Transparent"">
          <ContentPresenter HorizontalAlignment=""{{TemplateBinding HorizontalContentAlignment}}""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Hover")}""/></Trigger>
          <Trigger Property=""IsSelected"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""{C("Sel")}""/></Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Fg3")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
<Style TargetType=""PasswordBox"">
  <Setter Property=""Background"" Value=""{C("Bg3")}""/>
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""BorderBrush"" Value=""{C("Line")}""/>
  <Setter Property=""CaretBrush"" Value=""{C("Fg1")}""/>
  <Setter Property=""Padding"" Value=""6,4""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""PasswordBox"">
        <Border x:Name=""Bd"" Background=""{{TemplateBinding Background}}"" BorderBrush=""{{TemplateBinding BorderBrush}}"" BorderThickness=""1"" CornerRadius=""4"">
          <ScrollViewer x:Name=""PART_ContentHost"" Margin=""{{TemplateBinding Padding}}"" VerticalAlignment=""Center""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{C("Accent")}""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // List view itself
        yield return $@"
<Style TargetType=""ListView"">
  <Setter Property=""Background"" Value=""{C("Bg1")}""/>
  <Setter Property=""Foreground"" Value=""{C("Fg1")}""/>
  <Setter Property=""BorderThickness"" Value=""0""/>
</Style>
<Style TargetType=""GridSplitter"">
  <Setter Property=""Background"" Value=""{C("Line")}""/>
</Style>";
    }

    // ---------------- helpers for building UI in code ----------------

    public static TextBlock Icon(string glyph, double size = 14, string brush = "Fg1") => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(IconFont),
        FontSize = size,
        Foreground = Brush(brush),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Icon + optional label button.</summary>
    public static Button SmallButton(string glyph, string label, string tooltip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(glyph, 14));
        if (!string.IsNullOrEmpty(label))
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("Fg1") });
        return new Button { Content = panel, ToolTip = tooltip, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(8, 5, 8, 5), Focusable = false };
    }

    public static void SetButtonText(Button b, string text)
    {
        if (b.Content is StackPanel p && p.Children.Count > 1 && p.Children[1] is TextBlock t) t.Text = text;
    }

    /// <summary>A menu item that opens a submenu.</summary>
    public static MenuItem SubMenu(string header, string? glyph, IEnumerable<object> items, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (glyph != null) mi.Icon = Icon(glyph, 13, "Fg2");
        foreach (var i in items) mi.Items.Add(i);
        return mi;
    }

    /// <summary>Menu item showing an image (e.g. a file-type icon from the shell) instead of a glyph.</summary>
    public static MenuItem ImageMenuItem(string header, ImageSource? image, Action onClick, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (image != null)
            mi.Icon = new System.Windows.Controls.Image { Source = image, Width = 16, Height = 16, Stretch = Stretch.Uniform };
        if (gesture != null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    public static MenuItem MenuItem(string header, string? glyph, Action onClick, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (glyph != null) mi.Icon = Icon(glyph, 13, "Fg2");
        if (gesture != null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }
}
