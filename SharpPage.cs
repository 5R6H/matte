using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// The SHARP page (GlassPanel.xaml SharpPage) and SHARP's row on 存储与清理. One page in five states: check (only what
// can fall short; 显卡加速 with a card that can do it), license (Apple's terms; the tick unlocks 同意并下载), run (a
// bar, three steps, pause / cancel; errors take the subtitle), ready (what is waiting; 开始生成 or 以后再说 - Matte
// never starts making pictures by itself), and manage (an installed SHARP, from 存储与清理: its edition, switchable
// between CPU and 显卡, where it is, 移除 in two clicks). The install itself is SharpSetup.cs.
partial class App {
 string sharpView="check",sharpFrom="spatial",sharpPick="cpu";bool sharpWantGpu=true,sharpAgreed,sharpCancelArmed,sharpRemoveArmed;
 SharpCheck sharpCheck;System.Windows.Threading.DispatcherTimer sharpDisarm;long sharpBytes=-1;

 void CreateSharpPage(){
  Icon("SharpBackIcon",IBack);Icon("SharpGpuIcon",IChip);Icon("SharpReadIcon",IOpen);Icon("SharpReadyIcon",IImage);
  Icon("SharpEditionIcon",IChip);Icon("SharpWhereIcon",IFolder);
  P<ContentControl>("SharpAgreeTick").Content=IconVisual(ICheck);
  Click("SharpBack",LeaveSharp);
  Click("SharpGpuRow",()=>{sharpWantGpu=!sharpWantGpu;PaintSharp();});
  Click("SharpReadRow",()=>{try{System.Diagnostics.Process.Start(SharpLicenseUrl);}catch(Exception e){Log("License link: "+e.Message);}});
  Click("SharpAgreeRow",()=>{sharpAgreed=!sharpAgreed;PaintSharp();});
  Click("SharpLeftBtn",()=>SharpButton(false));Click("SharpRightBtn",()=>SharpButton(true));
  foreach(Button b in P<System.Windows.Controls.Primitives.UniformGrid>("SharpEditionSeg").Children){var tag=(string)b.Tag;b.Click+=delegate{sharpPick=tag;sharpRemoveArmed=false;PaintSharp();};}
  Click("SharpOpenBtn",()=>{try{if(System.IO.Directory.Exists(SharpRoot))System.Diagnostics.Process.Start("explorer.exe","\""+SharpRoot+"\"");}catch(Exception e){Log("Open SHARP: "+e.Message);}});
 }
 void OpenSharp(string from){
  sharpFrom=from;sharpCancelArmed=false;sharpRemoveArmed=false;
  sharpView=SharpBusy||sharpStage==SharpStage.Failed?"run":SharpInstalled?(from=="spatialstore"?"manage":"ready"):"check";
  if(sharpView=="check"){sharpCheck=CheckComputer(sharpWantGpu);if(!sharpCheck.GpuCan)sharpWantGpu=false;}
  if(sharpView=="manage"){sharpPick=SharpEditionTag;sharpCheck=CheckComputer(true);SharpMeasure();}
  if(sharpView=="ready")RefreshSpatial(true);
  PaintSharp();GoPage("sharp",1);
 }
 void LeaveSharp(){
  if(sharpView=="license"&&!SharpBusy){sharpView="check";PaintSharp();return;}
  GoPage(sharpFrom,-1);if(sharpFrom=="spatialstore")RefreshStore();else GenChanged();
 }
 // From the install thread (Post) and the page's own clicks.
 void SharpChanged(){
  if(panel==null)return;
  if(sharpStage==SharpStage.Done&&sharpView=="run"){sharpView="ready";sharpBytes=-1;RefreshSpatial(true);}
  if(page=="sharp")PaintSharp();
  PaintGenSummary();if(page=="spatialstore")PaintStoreSharp();
 }
 // what spatial\runtime takes, counted off the UI thread
 void SharpMeasure(){Task.Run(()=>UsedSoFar()).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{sharpBytes=t.Result;if(page=="sharp")PaintSharp();PaintStoreSharp();})));}
 static string Gb(long bytes){return (bytes/(double)(1L<<30)).ToString(bytes<10L*(1L<<30)?"0.0":"0")+" GB";}
 static string Minutes(double seconds){return seconds<90?"约 1 分钟":seconds<3600*1.5?"约 "+Math.Round(seconds/60)+" 分钟":"约 "+(seconds/3600).ToString("0.#")+" 小时";}

 void PaintSharp(){
  if(panel==null)return;
  var sub=P<TextBlock>("SharpSub");sub.Foreground=(Brush)panel.Resources["Ink2"];
  foreach(var n in new[]{"SharpCheckBox","SharpLicenseBox","SharpProgressBox","SharpReadyBox","SharpManageBox"})P<StackPanel>(n).Visibility=Visibility.Collapsed;
  var left=P<Button>("SharpLeftBtn");var right=P<Button>("SharpRightBtn");right.Style=(Style)panel.FindResource("Primary");right.IsEnabled=true;left.IsEnabled=true;
  var leftText=P<TextBlock>("SharpLeftText");leftText.Foreground=(Brush)panel.Resources["Ink"];
  Action<string,string> buttons=(l,r)=>{left.Visibility=l==null?Visibility.Collapsed:Visibility.Visible;P<Border>("SharpBtnGap").Visibility=left.Visibility;leftText.Text=l??"";
   right.Visibility=r==null?Visibility.Collapsed:Visibility.Visible;right.Content=r;};
  string title="安装 SHARP";
  if(sharpView=="check"){
   var c=sharpCheck??(sharpCheck=CheckComputer(sharpWantGpu));
   bool gpu=sharpWantGpu&&c.GpuCan;c=sharpCheck=CheckComputer(gpu);
   P<StackPanel>("SharpCheckBox").Visibility=Visibility.Visible;
   long download=(gpu?PackagesGpu:PackagesCpu)+(50L<<20)+WeightsBytes;
   sub.Text=SharpUnfinished?"上次没有装完 · 已下载的部分会接着用":"把照片变成 3D 的模型 · 下载约 "+Gb(download);
   Icon("SharpDiskIcon",c.DiskOk?ICheck:IAlert);var dv=P<TextBlock>("SharpDiskValue");
   dv.Text=c.DiskOk?"需要 "+Gb(c.Need):"还差 "+Gb(c.Need-c.Free);dv.Foreground=(Brush)panel.Resources[c.DiskOk?"Ink2":"Danger"];
   Icon("SharpRamIcon",c.RamOk?ICheck:IAlert);var rv=P<TextBlock>("SharpRamValue");
   rv.Text=c.RamOk?Math.Round(c.Ram/(double)(1L<<30))+" GB":Math.Round(c.Ram/(double)(1L<<30))+" GB · 建议 16 GB";rv.Foreground=(Brush)panel.Resources[c.RamOk?"Ink2":"Danger"];
   P<StackPanel>("SharpGpuBox").Visibility=c.GpuCan?Visibility.Visible:Visibility.Collapsed;
   if(c.GpuCan){PaintToggle("SharpGpu",sharpWantGpu);P<TextBlock>("SharpGpuInfo").Text="快约 2.5 倍 · 多下载 "+Gb(PackagesGpu-PackagesCpu);P<Button>("SharpGpuRow").ToolTip=c.Gpu+" · "+Math.Round(c.Vram/(double)(1L<<30))+" GB 显存";}
   buttons(null,SharpUnfinished?"继续安装":"下一步");right.IsEnabled=c.DiskOk;
  }else if(sharpView=="license"){
   title="Apple 的许可证";sub.Text="";
   P<StackPanel>("SharpLicenseBox").Visibility=Visibility.Visible;
   var box=P<Border>("SharpAgreeBox");box.Background=sharpAgreed?(Brush)panel.Resources["PrimaryBg"]:Brushes.Transparent;box.BorderBrush=(Brush)panel.Resources[sharpAgreed?"PrimaryBg":"Edge"];
   var tick=P<ContentControl>("SharpAgreeTick");tick.Visibility=sharpAgreed?Visibility.Visible:Visibility.Collapsed;tick.Foreground=(Brush)panel.Resources["Tone"];
   buttons("取消","同意并下载");right.IsEnabled=sharpAgreed;
  }else if(sharpView=="run"){
   P<StackPanel>("SharpProgressBox").Visibility=Visibility.Visible;
   bool failed=sharpStage==SharpStage.Failed;
   title=failed?"安装 SHARP":"正在安装 SHARP";
   double share=sharpTotal>0?Math.Min(1,sharpDone/(double)sharpTotal):0;
   var bar=P<Grid>("SharpBar");bar.ColumnDefinitions[0].Width=new GridLength(share,GridUnitType.Star);bar.ColumnDefinitions[1].Width=new GridLength(1-share,GridUnitType.Star);
   string amount=Gb(sharpDone).Replace(" GB","")+" / "+Gb(sharpTotal);
   if(failed){sub.Text=SharpErrorText();sub.Foreground=(Brush)panel.Resources["Danger"];}
   else if(sharpPaused)sub.Text="已暂停 · "+amount;
   else if(sharpStage==SharpStage.Trial)sub.Text="正在试运行…";
   else sub.Text=amount+SharpEta();
   // the three steps: done, now, still to come
   int now=sharpStage==SharpStage.Env?1:sharpStage==SharpStage.Model?2:sharpStage==SharpStage.Trial?3:failed?SharpFailedStep():4;
   for(int i=1;i<=3;i++){bool done=i<now,cur=i==now;
    Icon("SharpStep"+i+"Icon",done?ICheck:cur?(failed?IAlert:i==3?IDot:IDownload):IDot);
    P<TextBlock>("SharpStep"+i+"Text").Foreground=(Brush)panel.Resources[done||cur?"Ink":"Ink3"];
    P<TextBlock>("SharpStep"+i+"Value").Text="";}
   if(now==2&&!failed){long model=sharpDone-(sharpTotal-WeightsBytes);P<TextBlock>("SharpStep2Value").Text=sharpFoundModel?"用本机已有的":Math.Max(0,Math.Min(99,(int)(model*100/WeightsBytes)))+"%";}
   P<TextBlock>("SharpProgressNote").Visibility=failed?Visibility.Collapsed:Visibility.Visible;
   if(failed){string k=sharpErrorKind;buttons(k=="trial"||k=="env"||k=="other"||k=="files"?"查看日志":"取消",k=="verify"?"重新下载":k=="network"?"继续":"重试");}
   else{buttons(sharpCancelArmed?"确认取消":"取消",sharpPaused?"继续":"暂停");right.Style=(Style)panel.FindResource("Pill");}
  }else if(sharpView=="manage"){
   title="SHARP";P<StackPanel>("SharpManageBox").Visibility=Visibility.Visible;
   bool gpuNow=SharpEditionTag=="gpu",pickGpu=sharpPick=="gpu",canGpu=sharpCheck!=null&&sharpCheck.GpuCan;
   sub.Text=SharpEdition+(sharpBytes>0?" · "+Gb(sharpBytes):"")+" · 每张约 "+Math.Round(SharpSecondsPerPhoto)+" 秒";
   var seg=P<System.Windows.Controls.Primitives.UniformGrid>("SharpEditionSeg");PaintSeg(seg,b=>(string)b.Tag==sharpPick);
   var gpuBtn=(Button)seg.Children[1];gpuBtn.IsEnabled=canGpu||gpuNow;
   gpuBtn.ToolTip=canGpu?sharpCheck.Gpu+" · "+Math.Round(sharpCheck.Vram/(double)(1L<<30))+" GB 显存":"需要 8 GB 以上显存的 NVIDIA 显卡";
   string note=pickGpu==gpuNow
    ?(gpuNow?"用显卡生成"+(canGpu?" · "+sharpCheck.Gpu:""):canGpu?"这台电脑的显卡可以加速 · 显卡版每张快约 2.5 倍":"用 CPU 生成 · 显卡版需要 8 GB 以上显存的 NVIDIA 显卡")
    :pickGpu?"切到显卡版要再下载约 "+Gb(PackagesGpu-PackagesCpu)+" · 每张快约 2.5 倍":"切到 CPU 版会腾出约 "+Gb(DiskGpu-DiskCpu)+" · 每张慢约 2.5 倍";
   bool busyGen=Generating&&!CustomGenerator;
   if(busyGen)note="正在生成 Spatial 图片，结束后再切换或移除";
   else if(sharpRemoveArmed)note="再点一次移除 · 生成好的图片会保留";
   P<TextBlock>("SharpEditionNote").Text=note;
   P<TextBlock>("SharpWhere").Text=SharpRoot.Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"%LOCALAPPDATA%");
   buttons(sharpRemoveArmed?"确认移除":"移除 SHARP",pickGpu==gpuNow?null:pickGpu?"切换到显卡版":"切换到 CPU 版");
   if(sharpRemoveArmed)leftText.Foreground=(Brush)panel.Resources["Danger"];
   left.IsEnabled=!busyGen;right.IsEnabled=!busyGen;
  }else{
   title="SHARP 已就绪";
   P<StackPanel>("SharpReadyBox").Visibility=Visibility.Visible;
   double secs=SharpSecondsPerPhoto;sub.Text="每张约 "+Math.Round(secs)+" 秒 · 在后台生成";
   int todo=spatialNeeds==null?-1:spatialNeeds.Where(x=>!SpatialSkipped(x.Entry)).Sum(x=>x.Todo);
   P<TextBlock>("SharpReadyCount").Text=todo<0?"正在统计…":todo==0?"列表里的图片都有了":todo+" 张照片待生成";
   P<TextBlock>("SharpReadyTime").Text=todo>0?Minutes(todo*secs):"";
   if(todo>0)buttons("以后再说","开始生成");else buttons(null,"完成");
  }
  P<TextBlock>("SharpTitle").Text=title;sub.Visibility=string.IsNullOrEmpty(sub.Text)?Visibility.Collapsed:Visibility.Visible;
 }
 string SharpEta(){
  // from the speed since this run began (resumed bytes do not count)
  if(sharpDoneAtStart<0){sharpDoneAtStart=sharpDone;sharpStarted=DateTime.Now;return "";}
  double secs=(DateTime.Now-sharpStarted).TotalSeconds;long got=sharpDone-sharpDoneAtStart;
  if(secs<5||got<(4L<<20))return "";
  double left=(sharpTotal-sharpDone)/(got/secs)+30;   // and about half a minute for the trial
  return " · 还要"+Minutes(left);
 }
 int SharpFailedStep(){return sharpErrorKind=="trial"?3:System.IO.File.Exists(System.IO.Path.Combine(SharpRoot,"env","packages.ok"))?2:1;}
 string SharpErrorText(){
  switch(sharpErrorKind){
   case "network":return "下载中断了，进度已保存。";
   case "disk":{var c=CheckComputer(sharpGpu);return "空间不够，还差 "+Gb(Math.Max(1L<<28,c.Need-c.Free))+"。";}
   case "verify":return "文件损坏了，需要重新下载。";
   case "source":return "连不上 Apple 的服务器，稍后再试。";
   case "trial":return "装好了，但试运行没成功。";
   default:return "安装没有成功。";
  }
 }
 void SharpButton(bool rightButton){
  if(sharpView=="check"){
   if(SharpUnfinished){StartSharpInstall(System.IO.File.Exists(System.IO.Path.Combine(SharpRoot,"installing.txt"))&&System.IO.File.ReadAllText(System.IO.Path.Combine(SharpRoot,"installing.txt")).Contains("cu128"));sharpView="run";}
   else sharpView="license";
  }else if(sharpView=="license"){
   if(!rightButton){GoPage(sharpFrom,-1);return;}
   sharpView="run";StartSharpInstall(sharpWantGpu&&sharpCheck!=null&&sharpCheck.GpuCan);
  }else if(sharpView=="run"){
   if(sharpStage==SharpStage.Failed){
    if(rightButton){StartSharpInstall(sharpGpu);}
    else if(sharpErrorKind=="trial"||sharpErrorKind=="env"||sharpErrorKind=="other"||sharpErrorKind=="files"){
     foreach(var log in new[]{"trial.log","install.log"}){string p=System.IO.Path.Combine(SharpRoot,log);if(System.IO.File.Exists(p)){try{System.Diagnostics.Process.Start("notepad.exe","\""+p+"\"");}catch{}break;}}}
    else{CancelSharpInstall();GoPage(sharpFrom,-1);}
   }else if(rightButton)PauseSharpInstall(!sharpPaused);
   else if(!sharpCancelArmed){sharpCancelArmed=true;Disarm3s(()=>{sharpCancelArmed=false;PaintSharp();});}
   else{sharpCancelArmed=false;CancelSharpInstall();GoPage(sharpFrom,-1);}
  }else if(sharpView=="manage"){
   if(rightButton){
    // switching edition: only the PyTorch build changes; the model stays
    bool gpu=sharpPick=="gpu";var c=CheckComputer(gpu);
    if(gpu&&!c.DiskOk){P<TextBlock>("SharpEditionNote").Text="空间不够，还差 "+Gb(c.Need-c.Free)+"。";return;}
    sharpView="run";StartSharpInstall(gpu);
   }else if(!sharpRemoveArmed){sharpRemoveArmed=true;Disarm3s(()=>{sharpRemoveArmed=false;if(sharpView=="manage")PaintSharp();});}
   else{
    sharpRemoveArmed=false;P<Button>("SharpLeftBtn").IsEnabled=false;P<Button>("SharpRightBtn").IsEnabled=false;
    Task.Run(()=>RemoveSharp()).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
     sharpBytes=-1;sharpStage=SharpStage.None;Log("SHARP removed");
     GoPage("spatialstore",-1);RefreshStore();
     if(System.IO.Directory.Exists(SharpRoot))StoreMoveNote("没有全部移除：有文件正在使用，稍后再试",-1);
     GenChanged();})));
    return;
   }
  }else{
   if(rightButton&&P<Button>("SharpRightBtn").Content as string=="开始生成"){string why=StartGeneration();GoPage("spatial",-1);GenChanged();if(why!=null)GenNoteShow(why);}
   else{GoPage(sharpFrom=="spatialstore"?"spatialstore":"spatial",-1);GenChanged();}
  }
  PaintSharp();
 }
 void Disarm3s(Action after){
  if(sharpDisarm==null)sharpDisarm=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
  sharpDisarm.Stop();EventHandler h=null;h=delegate{sharpDisarm.Stop();sharpDisarm.Tick-=h;after();};sharpDisarm.Tick+=h;sharpDisarm.Start();
 }

 // The Spatial page's row before SHARP is there: what to do, or how far the install is.
 void PaintSharpEntry(TextBlock count,TextBlock note,Grid bar,int have,int photos){
  count.Text=photos>0?have+" / "+photos:"";
  double share=SharpBusy&&sharpTotal>0?sharpDone/(double)sharpTotal:photos>0?have/(double)photos:0;
  bar.ColumnDefinitions[0].Width=new GridLength(share,GridUnitType.Star);bar.ColumnDefinitions[1].Width=new GridLength(1-share,GridUnitType.Star);
  note.Text=SharpBusy?(sharpPaused?"SHARP 安装已暂停":"正在安装 SHARP · "+(int)(share*100)+"%"):sharpStage==SharpStage.Failed?"SHARP 没有装好 · 点这里查看":SharpUnfinished?"SHARP 没有装完 · 点这里继续":"需要先安装 SHARP";
 }
 // 存储与清理's SHARP row: installed (edition, size, 管理), being installed (查看), or 安装…; with a generator of the
 // user's own in the settings, just that.
 void PaintStoreSharp(){
  if(panel==null)return;var info=P<TextBlock>("StoreSharpInfo");var text=P<TextBlock>("StoreSharpBtnText");var btn=P<Button>("StoreSharpBtn");
  btn.IsEnabled=true;btn.Visibility=Visibility.Visible;
  if(SharpBusy){info.Text="正在安装 · "+(int)(sharpTotal>0?sharpDone*100/sharpTotal:0)+"%";text.Text="查看";return;}
  if(CustomGenerator){btn.Visibility=Visibility.Collapsed;info.Text="用设置里指定的生成工具";return;}
  if(!SharpInstalled){info.Text="未安装 · 在本机生成 Spatial 图片要用它";text.Text="安装…";return;}
  info.Text=SharpEdition+(sharpBytes>0?" · "+Gb(sharpBytes):"");text.Text="管理";
  if(sharpBytes<0){sharpBytes=0;SharpMeasure();}
 }
 void StoreSharpClicked(){OpenSharp("spatialstore");}
}
