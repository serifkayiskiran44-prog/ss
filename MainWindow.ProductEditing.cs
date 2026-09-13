using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        // #820: evaluate the whole record first; write each finding under its input, show the summary at the top,
        // focus the first blocker and refuse -- the store would refuse the same record, but without telling the
        // operator where.
        var view = FormValidationSummary.Compose(ProductValidation.Evaluate(edit), FormValidationSummary.ProductPropertyByField, EnteredProductValues());
        ApplyFormValidation(productEditor, view);
        ShowProductValidation(edit);
        if (!view.CanSave)
        {
            if (view.FirstBlocking is { CanFocus: true } first) FocusField(productEditor, first.Property, first.Section);
            throw new InvalidOperationException(view.Aggregate.Headline);
        }
        store.SaveProduct(edit); // Validation, optimistic version check and local transaction.
        productEditBaseline = JsonSerializer.Serialize(edit);
        ApplyFormValidation(productEditor, view with { Links = Array.Empty<FormValidationLink>() });
    }

    Dictionary<string, string> EnteredProductValues() => edit is null ? new() : new()
    {
        ["Name"] = edit.Name ?? "", ["Sku"] = edit.Sku ?? "", ["Barcode"] = edit.Barcode ?? "", ["Currency"] = edit.Currency ?? "",
        ["Description"] = edit.Description ?? "", ["Brand"] = edit.Brand ?? "", ["Category"] = edit.Category ?? "",
    };

    // Writes each link's message under its input (blocking or warning style) and clears the rows no finding names.
    void ApplyFormValidation(Panel form, FormValidationView view)
    {
        foreach (var row in formRows.Where(r => r.Key.Form == form)) row.Value.SetValidation("");
        foreach (var link in view.Links.Where(l => l.CanFocus))
            if (formRows.TryGetValue((form, link.Property), out var row)) row.SetValidation(link.Message, link.Level);
    }

    // Brings the section into view when the form has sections, then puts keyboard focus on the input.
    void FocusField(Panel form, string property, string? section)
    {
        if (!formRows.TryGetValue((form, property), out var row)) return;
        // Select the section that actually holds the input: walk the *logical* tree up to its TabItem (a tab's
        // content is presented by the TabControl's ContentPresenter, so the visual chain skips the TabItem and
        // would land on the outer route page). The finding's section name is only the fallback.
        DependencyObject? node = row.Root; TabItem? owner = null;
        while (node is not null && owner is null) { node = LogicalTreeHelper.GetParent(node) ?? (node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : null); owner = node as TabItem; }
        if (owner?.Tag is string key) SelectProductSection(key);
        else if (!string.IsNullOrEmpty(section) && form == productEditor) SelectProductSection(section);
        row.Input.BringIntoView();
        // A section switch realizes its content on the next layout pass; focus asked for before that is refused,
        // so try now and again once the tree is loaded.
        if (!Keyboard.Focus(row.Input)?.Equals(row.Input) ?? true)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { row.Input.BringIntoView(); Keyboard.Focus(row.Input); }));
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
        var body = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage) };
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
