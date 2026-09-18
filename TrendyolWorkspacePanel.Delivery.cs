using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolDeliveryTemplateRow(TrendyolDeliveryTemplate Template,string CarrierName,string DurationLabel,string ShipmentName,string ReturningName,int ProductCount)
{
    public string Name=>Template.Name;
    public string DesiSource=>Template.IncludeProductDesi?"Ürün kartından":"Gönderme";
}

public sealed partial class TrendyolWorkspacePanel
{
    readonly DataGrid templates=Grid("TrendyolTemplates",("Şablon adı","Name",170),("Kargo firması","CarrierName",150),("Termin süresi","DurationLabel",140),("Desi kaynağı","DesiSource",120),("Sevkiyat adresi","ShipmentName",200),("İade adresi","ReturningName",200),("Ürün","ProductCount",60));
    readonly TextBox templateName=Box();
    readonly ComboBox templateDesi=new(){Name="TrendyolTemplateDesi",ItemsSource=new[]{"Ürün kartından al","Gönderme · mevcut desiyi koru"},SelectedIndex=0,Margin=new(3)};
    readonly ComboBox carrier=Combo(),shipment=Combo(),returning=Combo();
    readonly ComboBox duration=new(){ItemsSource=new[]{"Mağaza varsayılanı"}.Concat(Enumerable.Range(0,31).Select(i=>i==0?"0 · Bugün kargoda":i==1?"1 · En geç yarın kargoda":i+" gün")).ToArray(),SelectedIndex=0,Margin=new(3)};
    readonly TextBlock templateHeading=T("Yeni teslimat şablonu"),deliveryCacheStatus=T("",true),templateEmpty=T("Henüz şablon yok. Sağdaki forma bir ad vererek ilk teslimat şablonunuzu kaydedin.",true);
    string? templateId;
    bool refreshingTemplates;

    FrameworkElement BuildDelivery()
    {
        templateName.Name="TrendyolTemplateName";carrier.Name="TrendyolTemplateCarrier";duration.Name="TrendyolTemplateDuration";shipment.Name="TrendyolTemplateShipment";returning.Name="TrendyolTemplateReturning";
        templates.SelectionMode=DataGridSelectionMode.Single;templates.RowHeight=32;templates.Background=System.Windows.Media.Brushes.White;
        var dock=new DockPanel{Margin=new(4)};var top=new StackPanel();
        top.Children.Add(T("Teslimat şablonları",false));
        top.Children.Add(T("1. Şablonu kaydedin   →   2. Seçili ürünlere atayın   →   3. Değişiklikleri önizleyip gönderin",true));
        var actions=new WrapPanel();actions.Children.Add(B("Yeni şablon",NewDeliveryTemplate));actions.Children.Add(A("Kargo ve adresleri API'den al",RefreshDeliveryOptions));actions.Children.Add(B("Ürünlere atamaya geç",()=>{tabs.SelectedIndex=0;ShowProductView("list");bulkActions.Visibility=Visibility.Visible;}));top.Children.Add(actions);top.Children.Add(deliveryCacheStatus);DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var editor=new StackPanel();templateHeading.FontWeight=FontWeights.SemiBold;editor.Children.Add(templateHeading);
        Field(editor,"Şablon adı",templateName);Field(editor,"Kargo firması",carrier);Field(editor,"Termin süresi (gün)",duration);Field(editor,"Desi bilgisi",templateDesi);editor.Children.Add(T("Her ürünün kendi desisi kullanılır. Boş desi gönderilmez; değer ürün kartından, XML veya Excel’den girilir.",true));Field(editor,"Sevkiyat adresi",shipment);Field(editor,"İade adresi",returning);
        editor.Children.Add(T("Mağaza varsayılanı seçilen alanlar gönderilmez. Mevcut kargo ve adres bilgileri korunur.",true));
        var saveActions=new WrapPanel();saveActions.Children.Add(Primary(B("Şablonu kaydet",SaveDeliveryTemplate)));saveActions.Children.Add(B("Seçili şablonu sil",DeleteDeliveryTemplate));editor.Children.Add(saveActions);
        editor.Children.Add(T("Şablonu kaydetmek Trendyol'a gönderim yapmaz. Ürün ataması ve gönderim ayrı adımlardır.",true));
        var editorFrame=Section("Şablon bilgileri",Scroll(editor));editorFrame.Width=350;DockPanel.SetDock(editorFrame,Dock.Right);dock.Children.Add(editorFrame);
        var list=new DockPanel();DockPanel.SetDock(templateEmpty,Dock.Top);list.Children.Add(templateEmpty);list.Children.Add(templates);dock.Children.Add(list);
        templates.SelectionChanged+=(_,_)=>{if(!refreshingTemplates&&templates.SelectedItem is TrendyolDeliveryTemplateRow row)LoadDeliveryTemplate(row.Template);};
        return dock;
    }

    TrendyolDeliveryTemplate ReadDeliveryTemplate()=>new(templateId??"",templateName.Text.Trim(),(carrier.SelectedItem as TrendyolCarrier)?.Code??"",duration.SelectedIndex>0?duration.SelectedIndex-1:null,shipment.SelectedItem is TrendyolAddress {Id:>0} s?s.Id:null,returning.SelectedItem is TrendyolAddress {Id:>0} r?r.Id:null){IncludeProductDesi=templateDesi.SelectedIndex==0};
    void LoadDeliveryTemplate(TrendyolDeliveryTemplate value)
    {
        templateId=value.Id.Length==0?null:value.Id;templateName.Text=value.Name;templateHeading.Text=templateId is null?"Yeni teslimat şablonu":"Şablonu düzenle";
        carrier.SelectedItem=carrier.Items.Cast<TrendyolCarrier>().FirstOrDefault(c=>c.Code==value.CarrierCode);
        duration.SelectedIndex=value.DurationDays.HasValue?value.DurationDays.Value+1:0;templateDesi.SelectedIndex=value.IncludeProductDesi?0:1;
        shipment.SelectedItem=shipment.Items.Cast<TrendyolAddress>().FirstOrDefault(a=>a.Id==(value.ShipmentAddressId??0));
        returning.SelectedItem=returning.Items.Cast<TrendyolAddress>().FirstOrDefault(a=>a.Id==(value.ReturningAddressId??0));
    }
    void NewDeliveryTemplate(){templates.SelectedItem=null;LoadDeliveryTemplate(new("","","",null,null,null));templateName.Focus();}
    void SaveDeliveryTemplate()
    {
        var value=ReadDeliveryTemplate();if(value.Name.Length==0)throw new InvalidOperationException("Şablon adını yazın.");
        if(value.Id.Length==0)value=value with{Id=Guid.NewGuid().ToString("N")};
        var next=CurrentDeliveryState();if(next.Templates.Any(t=>t.Id!=value.Id&&t.Name.Equals(value.Name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Bu şablon adı kullanılıyor.");
        next.Templates.RemoveAll(t=>t.Id==value.Id);next.Templates.Add(value);store.Save(next);state=store.Load(next.SellerId);InvalidatePreview();RefreshTemplates(value);RefreshDeliveryPickers(value.Id);
        status.Text=$"'{value.Name}' kaydedildi. Kontrol listesindeki seçili ürünlere atayabilirsiniz; henüz gönderim yapılmadı.";
    }
    void DeleteDeliveryTemplate()
    {
        var row=templates.SelectedItem as TrendyolDeliveryTemplateRow??throw new InvalidOperationException("Şablon seçin.");var next=CurrentDeliveryState();
        if(next.Profiles.Any(p=>p.DeliveryTemplateId==row.Template.Id))throw new InvalidOperationException("Bu şablon ürünlerde kullanılıyor; önce ürün atamalarını değiştirin.");
        if(MessageBox.Show(Window.GetWindow(this),$"'{row.Name}' teslimat şablonu silinsin mi?","Teslimat şablonu",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        next.Templates.RemoveAll(t=>t.Id==row.Template.Id);store.Save(next);state=store.Load(next.SellerId);InvalidatePreview();RefreshTemplates();RefreshDeliveryPickers();status.Text="Şablon silindi.";
    }
    TrendyolWorkspaceState CurrentDeliveryState()
    {
        var next=store.Load(Account().SupplierId);if(next.SellerId!=state.SellerId||next.Revision!=state.Revision)throw new InvalidOperationException("Mağaza ayarları değişti; ekranı yenileyip tekrar deneyin.");return next;
    }
    async Task RefreshDeliveryOptions()
    {
        var draft=ReadDeliveryTemplate();var account=Account();var next=CurrentDeliveryState();using var client=new TrendyolApiClient(account);
        var addresses=await client.GetAddressesAsync(Token);var carriers=await client.GetCarriersAsync(Token);EnsureAccount(account);
        next.Addresses=addresses.ToList();next.Carriers=carriers.ToList();next.AddressesUpdatedUtc=DateTime.UtcNow;store.Save(next);state=store.Load(account.SupplierId);InvalidatePreview();RefreshTemplates(draft);RefreshDeliveryPickers();status.Text="Kargo ve adresler yenilendi. Formdaki seçimler korundu.";
    }
    void RefreshTemplates(TrendyolDeliveryTemplate? draft=null)
    {
        refreshingTemplates=true;try{
            var all=state.Templates.Concat(draft is null?Array.Empty<TrendyolDeliveryTemplate>():new[]{draft}).ToArray();
            // Retain cached assignments even when an address/carrier is no longer returned by the API.
            carrier.ItemsSource=new[]{new TrendyolCarrier("","(Mağaza varsayılanı)")}.Concat(state.Carriers).Concat(all.Where(t=>t.CarrierCode.Length>0&&!state.Carriers.Any(c=>c.Code==t.CarrierCode)).Select(t=>new TrendyolCarrier(t.CarrierCode,t.CarrierCode+" (listede yok)"))).DistinctBy(c=>c.Code).ToList();
            List<TrendyolAddress> AddressOptions(bool isShipment)=>new[]{new TrendyolAddress(0,"(Mağaza varsayılanı)",isShipment,!isShipment)}.Concat(state.Addresses.Where(a=>isShipment?a.IsShipment:a.IsReturning)).Concat(all.Select(t=>isShipment?t.ShipmentAddressId:t.ReturningAddressId).Where(id=>id.HasValue&&!state.Addresses.Any(a=>a.Id==id&&(isShipment?a.IsShipment:a.IsReturning))).Select(id=>new TrendyolAddress(id!.Value,$"Adres {id} (listede yok)",isShipment,!isShipment))).DistinctBy(a=>a.Id).ToList();
            shipment.ItemsSource=AddressOptions(true);returning.ItemsSource=AddressOptions(false);
            string AddressName(long? id)=>id.HasValue?state.Addresses.FirstOrDefault(a=>a.Id==id)?.Name??$"Adres {id} (listede yok)":"Mağaza varsayılanı";
            templates.ItemsSource=state.Templates.Select(t=>new TrendyolDeliveryTemplateRow(t,t.CarrierCode.Length==0?"Mağaza varsayılanı":state.Carriers.FirstOrDefault(c=>c.Code==t.CarrierCode)?.Name??t.CarrierCode,t.DurationDays switch{null=>"Mağaza varsayılanı",0=>"Bugün kargoda",1=>"En geç yarın",_=>t.DurationDays+" gün"},AddressName(t.ShipmentAddressId),AddressName(t.ReturningAddressId),state.Profiles.Count(p=>p.DeliveryTemplateId==t.Id))).ToList();
            templates.SelectedItem=templates.Items.Cast<TrendyolDeliveryTemplateRow>().FirstOrDefault(t=>t.Template.Id==draft?.Id);LoadDeliveryTemplate(draft??new("","","",null,null,null));
            templateEmpty.Visibility=state.Templates.Count==0?Visibility.Visible:Visibility.Collapsed;
            deliveryCacheStatus.Text=$"{state.Templates.Count} şablon · {state.Carriers.Count} kargo firması · {state.Addresses.Count(a=>a.IsShipment)} sevkiyat / {state.Addresses.Count(a=>a.IsReturning)} iade adresi · Son güncelleme: {Time(state.AddressesUpdatedUtc)}";
        }finally{refreshingTemplates=false;}
    }
    void RefreshDeliveryPickers(string? selectedId=null)
    {
        var bulkId=selectedId??(bulkDelivery.SelectedItem as TrendyolDeliveryTemplate)?.Id;var productId=(delivery.SelectedItem as TrendyolDeliveryTemplate)?.Id??"";
        var wasPopulating=populating;populating=true;try{
            bulkDelivery.ItemsSource=state.Templates.ToList();bulkDelivery.SelectedItem=state.Templates.FirstOrDefault(t=>t.Id==bulkId)??state.Templates.FirstOrDefault();
            delivery.ItemsSource=new[]{new TrendyolDeliveryTemplate("","(Mağaza varsayılanı)","",null,null,null)}.Concat(state.Templates).ToList();delivery.SelectedItem=delivery.Items.Cast<TrendyolDeliveryTemplate>().FirstOrDefault(t=>t.Id==productId)??delivery.Items[0];
            if(!editorDirty)editorRevision=state.Revision;
        }finally{populating=wasPopulating;}
    }
}
