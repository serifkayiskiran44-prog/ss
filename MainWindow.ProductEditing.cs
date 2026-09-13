using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    string? productEditBaseline;
    bool changingProductSelection;
    bool resolvingProductEdit;
    List<CatalogProduct> acceptedProductSelection = new();

    bool ProductEditIsDirty()
    {
        if (edit == null) return false;
        // Invalid text may not reach the model, but still belongs to the user's draft.
        try { ValidBindings(productEditor); }
        catch (InvalidOperationException) { return true; }
        return JsonSerializer.Serialize(edit) != productEditBaseline;
    }

    void SaveProductEdit()
    {
        ValidBindings(productEditor);
        if (edit == null) return;
        store.SaveProduct(edit); // Validation, optimistic version check and local transaction.
        productEditBaseline = JsonSerializer.Serialize(edit);
    }

    bool ResolveProductEdit()
    {
        // ShowDialog pumps the dispatcher: an in-flight search or timer can return here.
        if (resolvingProductEdit) return false;
        if (!ProductEditIsDirty()) return true;
        resolvingProductEdit = true;
        try
        {
            var decision = AskProductEditDecision();
            if (decision == "Vazgeç") return true;
            if (decision != "Kaydet") return false;
            SaveProductEdit();
            return true;
        }
        catch (Exception ex) { Log(Safe(ex), NotificationSeverity.Error); return false; }
        finally { resolvingProductEdit = false; }
    }

    string AskProductEditDecision()
    {
        var decision = "İptal";
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(new TextBlock {
            Text = $"{edit?.Sku}: Kaydedilmemiş değişiklikler var.\nKaydetmeden çıkmak istiyor musun?",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 400, Margin = new Thickness(0, 0, 0, 16)
        });
        // #818: the standard shell; discarding is destructive, so Enter stays on İptal as before.
        var dialog = DialogShell.Create(this, "Kaydedilmemiş değişiklikler", body, new DialogShell.Action[]
        {
            new("İptal", IsCancel: true, OnClick: () => { decision = "İptal"; return true; }),
            new("Vazgeç", OnClick: () => { decision = "Vazgeç"; return true; }),
            new("Kaydet", IsPrimary: true, OnClick: () => { decision = "Kaydet"; return true; }),
        }, 460, 240, destructive: true);
        dialog.ShowDialog();
        return decision;
    }

    void ProductSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingProductSelection) return;
        var next = products.SelectedItem as CatalogProduct;
        if (next?.Id == edit?.Id)
        {
            acceptedProductSelection = products.SelectedItems.OfType<CatalogProduct>().ToList();
            return;
        }
        if (!ResolveProductEdit())
        {
            changingProductSelection = true;
            try
            {
                products.SelectedItems.Clear();
                foreach (var row in acceptedProductSelection) products.SelectedItems.Add(row);
            }
            finally { changingProductSelection = false; }
            return;
        }
        BindProductEdit(next);
    }

    void BindProductEdit(CatalogProduct? row)
    {
        // Grid rows may predate an explicit Save decision. Read the current persisted version.
        edit = row == null ? null : store.FindProduct(row.Id);
        productEditBaseline = edit == null ? null : JsonSerializer.Serialize(edit);
        productEditor.DataContext = edit;
        productEditor.IsEnabled = edit != null;
        ShowProductChannelStatus(edit);
        ShowProductPriceSummary(edit);
        ShowProductStockSummary(edit);
        ShowProductProvenance(edit);
        ShowProductValidation(edit);
        ShowProductAuditTimeline(edit);
        RefreshProductDirtyIndicator();
        RefreshPriceFieldsPanel();
        acceptedProductSelection = products.SelectedItems.OfType<CatalogProduct>().ToList();
    }
}
