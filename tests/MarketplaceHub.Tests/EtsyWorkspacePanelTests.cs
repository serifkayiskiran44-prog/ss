using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public class EtsyWorkspacePanelTests
{
    [TestMethod]
    public void EtsyWorkspaceHasControlSettingsHistoryAndNoImplicitSend()
    {
        InSta(dir =>
        {
            var type = typeof(MainWindow).Assembly.GetType("TrMarketplaceHubDesktop.EtsyWorkspacePanel");
            Assert.IsNotNull(type, "Etsy needs a visible workspace rather than inaccessible legacy tabs.");
            var panel = (FrameworkElement)Activator.CreateInstance(type, new object[] { dir });
            using var lifetime = (IDisposable)panel;
            var controls = Walk(panel).ToList();
            var tabs = controls.OfType<TabControl>().Single(t=>t.Name=="EtsySections");
            CollectionAssert.AreEqual(new[]{"Etsy kontrol","Ayarlar","İşlem geçmişi"}, tabs.Items.Cast<TabItem>().Select(t=>t.Header.ToString()).ToArray());
            Assert.IsFalse(controls.OfType<Button>().Single(b=>b.Name=="EtsySend").IsEnabled);
            Assert.IsTrue(controls.OfType<Button>().Any(b=>b.Name=="EtsyOpenSettings"));
            Assert.IsTrue(controls.OfType<PasswordBox>().Count()>=3);
        });
    }

    [TestMethod]
    public void EtsyExpandedActionsKeepProductListUsable()
    {
        InSta(dir =>
        {
            var type = typeof(MainWindow).Assembly.GetType("TrMarketplaceHubDesktop.EtsyWorkspacePanel");
            Assert.IsNotNull(type);
            var panel = (FrameworkElement)Activator.CreateInstance(type, new object[] { dir });
            using var lifetime = (IDisposable)panel;
            foreach(var name in new[]{"EtsyToggleBulk","EtsyToggleFilters"})
                Walk(panel).OfType<Button>().Single(b=>b.Name==name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.Measure(new Size(1140,600));panel.Arrange(new Rect(0,0,1140,600));panel.UpdateLayout();
            var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="EtsyProducts");
            Assert.IsTrue(grid.ActualHeight>=235, $"Product grid must retain usable height: {grid.ActualHeight}");
            Assert.AreEqual(2,grid.FrozenColumnCount);
        });
    }

    [TestMethod]
    public void EtsyIsVisibleUnderIntegrationsInMainWindow()
    {
        InSta(dir =>
        {
            var window=new MainWindow(dir);
            try
            {
                var nav=Walk(window).OfType<ListBox>().Single(l=>l.Name=="NavigationList");
                Assert.IsTrue(nav.Items.OfType<ListBoxItem>().Any(i=>Equals(i.Tag,"etsy") && Equals(i.Content,"Etsy")));
            }
            finally { window.Close(); }
        });
    }

    static void InSta(Action<string> action)
    {
        Exception failure=null;
        var thread=new Thread(()=>
        {
            var dir=Path.Combine(Path.GetTempPath(),"etsy-panel-"+Guid.NewGuid().ToString("N"));
            try{action(dir);}catch(Exception e){failure=e;}
            finally
            {
                // MainWindow can still be completing its cancelled search-index warmup.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                for(var attempt=0;attempt<20;attempt++)
                {
                    SqliteConnection.ClearAllPools();
                    try { if(Directory.Exists(dir))Directory.Delete(dir,true);break; }
                    catch(IOException) when(attempt<19) { Thread.Sleep(50); }
                    catch(Exception e) { failure ??= e; break; }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var item in Walk(child))yield return item;
    }
}
