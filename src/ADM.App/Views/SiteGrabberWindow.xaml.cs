using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Adm.App.ViewModels;
using Adm.Core.Util;

namespace Adm.App.Views;

public partial class SiteGrabberWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly HttpClient _client = new();

    public SiteGrabberWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Owner = Application.Current.MainWindow;
    }

    private async void OnGrab(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            string html = await _client.GetStringAsync(url);
            var links = SiteGrabber.ExtractLinks(html, url);
            LinksList.ItemsSource = links;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnGrab(sender, e);
    }

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        if (LinksList.SelectedItems.Count == 0) return;
        var list = new List<string>();
        foreach (var item in LinksList.SelectedItems)
            list.Add(item.ToString());
        string text = string.Join(Environment.NewLine, list);
        _vm.AddUrls(text);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
