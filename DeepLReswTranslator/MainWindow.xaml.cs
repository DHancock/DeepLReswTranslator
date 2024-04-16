using DeepL;
using DeepL.Model;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Win32;
using Windows.Win32.Foundation;

using WinRT.Interop;

namespace DeepLReswTranslator;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr windowPtr;
    private StorageFile? sourceFile;

    public MainWindow()
    {
        this.InitializeComponent();
        windowPtr = WindowNative.GetWindowHandle(this);

        AppWindow.Title = "DeepL Resw Translator";

        CenterInPrimaryDisplay(clientWidth: 1000, clientHeight: 255);
    }

    private void CenterInPrimaryDisplay(int clientWidth, int clientHeight)
    {
        double scaleFactor = PInvoke.GetDpiForWindow((HWND)windowPtr) / 96.0;

        int deviceWidth = (int)(clientWidth * scaleFactor);
        int deviceHeight = (int)(clientHeight * scaleFactor);

        RectInt32 windowArea;
        RectInt32 workArea = DisplayArea.Primary.WorkArea;

        windowArea.X = Math.Max((workArea.Width - deviceWidth) / 2, workArea.X);
        windowArea.Y = Math.Max((workArea.Height - deviceHeight) / 2, workArea.Y);
        windowArea.Width = deviceWidth;
        windowArea.Height = deviceHeight;

        AppWindow.MoveAndResize(windowArea);
    }

    private async Task AttemptLoadLanguages(string authKey)
    {
        try
        {
            // the translator object is tied to a perticular key, whether valid or not
            using (Translator translator = new Translator(authKey))
            {
                List<LanguageData> source = await LoadLanguages(translator, isSource: true);

                if (source.Count > 0)
                {
                    List<LanguageData> target = await LoadLanguages(translator, isSource: false);

                    if ((target.Count > 0) && (FromLanguage.ItemsSource is null))
                    {
                        FromLanguage.ItemsSource = source;
                        ToLanguage.ItemsSource = target;

                        FromLanguage.SelectedIndex = source.FindIndex(x => x.Code == "en");
                        ToLanguage.SelectedIndex = target.FindIndex(x => x.Code == "fr");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    private async void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker openPicker = new FileOpenPicker();
        InitializeWithWindow.Initialize(openPicker, windowPtr);
        openPicker.FileTypeFilter.Add(".resw");

        StorageFile newSource = await openPicker.PickSingleFileAsync();

        if (newSource is not null)
        {
            sourceFile = newSource;
            SourceReswPath.Text = sourceFile.Path;
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await IsDataValid())
            {
                XDocument? document;

                using (Stream stream = await sourceFile.OpenStreamForReadAsync())
                {
                    try
                    {
                        document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        await ErrorDialog("Loading source resw failed", ex);
                        return;
                    }
                }

                if (await ValidateReswVersion(document))
                {
                    List<string> source = ParseSource(document);
                    List<string> translated = await TranslateSource(source);

                    if (translated.Count == source.Count)  // translation succeeded
                    {
                        FileSavePicker savePicker = new FileSavePicker();
                        InitializeWithWindow.Initialize(savePicker, windowPtr);
                        savePicker.FileTypeChoices.Add("resw file", [".resw"]);

                        savePicker.SuggestedFileName = sourceFile?.Name;

                        StorageFile outputFile = await savePicker.PickSaveFileAsync();

                        if (outputFile is not null)
                        {
                            await WriteToOutputResw(document, translated, outputFile);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await ErrorDialog("General failure:", ex);
        }
    }

    private static List<string> ParseSource(XDocument document)
    {
        List<string> source = [];

        foreach (XElement data in document.Descendants("data"))
        {
            IEnumerable<XElement> values = data.Descendants("value");
            Debug.Assert(values.Count() == 1);

            foreach (XElement value in values)
            {
                Debug.Assert(!string.IsNullOrEmpty(value.Value));
                source.Add(value.Value);
            }
        }

        return source;
    }

    private async Task<bool> ValidateReswVersion(XDocument document)
    {
        if (document.Root is not null)
        {
            foreach (XElement resHeader in document.Descendants("resheader"))
            {
                XAttribute? nameAttrib = resHeader.Attribute("name");

                if ((nameAttrib != null) && (nameAttrib.Value == "version") && (resHeader.Value == "2.0"))
                {
                    return true;
                }
            }
        }

        await ErrorDialog("Invalid resw xml");
        return false;
    }

    private static async Task<List<LanguageData>> LoadLanguages(Translator translator, bool isSource)
    {
        Language[] result = isSource ? await translator.GetSourceLanguagesAsync() : await translator.GetTargetLanguagesAsync();

        List<LanguageData> output = new List<LanguageData>(result.Length);

        foreach (Language language in result)
        {
            output.Add(new LanguageData(language.Name, language.Code));
        }

        return output;
    }

    private record LanguageData (string Name, string Code)
    {
        public override string ToString() => $"{Code} - {Name}";
    }

    private async Task<List<string>> TranslateSource(List<string> source)
    {
        List<string> results = new List<string>(source.Count);

        if (source.Count > 0)
        {
            try
            {
                string from = ((LanguageData)FromLanguage.SelectedItem).Code;
                string to = ((LanguageData)ToLanguage.SelectedItem).Code;

                // the translator object is tied to a perticular key, whether valid or not
                using (Translator translator = new Translator(Key.Text))
                {
                    TextResult[] textResults = await translator.TranslateTextAsync(source, from, to);

                    results.EnsureCapacity(textResults.Length);

                    foreach (TextResult textResult in textResults)
                    {
                        results.Add(textResult.Text);
                    }
                }
            }
            catch (Exception ex)
            {
                await ErrorDialog("Translation failed", ex);
            }
        }

        return results;
    }

    private static async Task WriteToOutputResw(XDocument document, List<string> text, StorageFile outputFile)
    {
        int index = 0;

        foreach (XElement data in document.Descendants("data"))
        {
            IEnumerable<XElement> values = data.Descendants("value");
            Debug.Assert(values.Count() == 1);

            foreach (XElement value in values)
            {
                value.Value = text[index++];
            }
        }

        using (Stream stream = await outputFile.OpenStreamForWriteAsync())
        {
            await document.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
            stream.SetLength(stream.Position);
        }
    }

    private async Task<bool> IsDataValid()
    {
        if (string.IsNullOrWhiteSpace(Key.Text))
        {
            await ErrorDialog("key is invalid");
            return false;
        }

        if (string.IsNullOrWhiteSpace(SourceReswPath.Text))
        {
            await ErrorDialog("Source file is empty");
            return false;
        }

        if ((sourceFile == null) || !string.Equals(sourceFile.Path, SourceReswPath.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                sourceFile = await StorageFile.GetFileFromPathAsync(SourceReswPath.Text.Trim());
            }
            catch
            {
                await ErrorDialog("Source file path is invalid");
                return false;
            }
        }

        if ((FromLanguage.SelectedIndex < 0) || (ToLanguage.SelectedIndex < 0))
        {
            await ErrorDialog("languages are invalid, check the auth key");
            return false;
        }

        if (FromLanguage.SelectedIndex == ToLanguage.SelectedIndex)
        {
            await ErrorDialog("languages are equal");
            return false;
        }

        return true;
    }

    private async void Key_TextChanged(object sender, TextChangedEventArgs e)
    {
        TextBox tb = (TextBox)sender;

        if (!string.IsNullOrWhiteSpace(tb.Text) && (FromLanguage.ItemsSource is null))
        {
            await AttemptLoadLanguages(tb.Text);
        }
    }

    private async Task ErrorDialog(string message, Exception? details = null)
    {
        ContentDialog cd = new ContentDialog()
        {
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            XamlRoot = Content.XamlRoot,
            Title = AppWindow.Title,
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            Content = details is null ? message : $"{message}{Environment.NewLine}{details}",
        };

        await cd.ShowAsync();
    }
}
