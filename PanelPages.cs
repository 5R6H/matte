using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path=System.Windows.Shapes.Path;
using SD=System.Drawing;

// The panel's settings page (每张停留, Spatial 镜头, 面板背景) and playlist page, and the two panel styles. Pages slide like
// the colour page (SlidePages in PanelUI.cs); these two are Collapsed until opened and then take the menu page's height,
// so the panel never changes size.
partial class App {
 string page="menu";
 // ------------------------------------------------------------ Spatial page: on / off, camera, Spatial 图片
 List<EntryNeeds> spatialNeeds;int needsGeneration;
 void RefreshSpatial(bool recount){
  if(panel==null)return;
  PaintToggle("SpatialOn",spatialEnabled);
  P<TextBlock>("SpatialState").Text=!spatialEnabled?"已关闭 · 显示普通照片":!SpatialPlays?"列表里还没有 Spatial 图片":"已开启";
  P<StackPanel>("MotionBox").Opacity=spatialEnabled?1:.45;   // still choosable, but it shows it does nothing now
  RefreshSettings();   // the camera choice (MotionSeg)
  if(recount&&!Generating){int g=++needsGeneration;Task.Run(()=>SpatialNeedsByEntry()).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{if(g==needsGeneration&&t.Exception==null){spatialNeeds=t.Result;RefreshGenList();GenChanged();}})));}
  RefreshGenList();GenChanged();
 }
 // One compact line per playlist entry: a tick (made by 生成, or left out), its name, a short bar of how much of it has
 // Spatial pictures, and the count. The ones with pictures still to make come first; the finished ones follow, quieter.
 // A click on the row ticks / unticks it (remembered).
 void RefreshGenList(){
  var list=P<StackPanel>("GenList");list.Children.Clear();
  if(spatialNeeds==null){list.Children.Add(new TextBlock{Text="正在统计…",FontSize=12,Foreground=(Brush)panel.Resources["Ink3"],Margin=new Thickness(10,8,0,8)});return;}
  if(spatialNeeds.Count==0){list.Children.Add(new TextBlock{Text="播放列表是空的",FontSize=12,Foreground=(Brush)panel.Resources["Ink3"],Margin=new Thickness(10,8,0,8)});return;}
  foreach(var n in spatialNeeds.OrderBy(x=>x.Todo==0?1:0)){var entry=n;bool on=!SpatialSkipped(entry.Entry),done=entry.Todo==0;
   var g=new Grid();foreach(var w in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})g.ColumnDefinitions.Add(new ColumnDefinition{Width=w});
   // the tick: filled (in) or an empty ring (left out); nothing left to make shows a quiet check
   var tick=new Grid{Width=16,Height=16,Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center};
   tick.Children.Add(new Ellipse{Fill=on&&!done?(Brush)panel.Resources["PrimaryBg"]:Brushes.Transparent,Stroke=(Brush)panel.Resources[on&&!done?"PrimaryBg":"Edge"],StrokeThickness=1.2});
   if(on||done)tick.Children.Add(new ContentControl{Width=10,Height=10,Content=IconVisual(ICheck),Foreground=done?(Brush)panel.Resources["Ink3"]:(Brush)panel.Resources["Tone"]});
   var name=new TextBlock{Text=System.IO.Path.GetFileName(entry.Entry.TrimEnd('\\')),FontSize=13,VerticalAlignment=VerticalAlignment.Center,Foreground=(Brush)panel.Resources[done?"Ink2":on?"Ink":"Ink3"],ToolTip=entry.Entry};Grid.SetColumn(name,1);
   var bar=new Grid{Width=40,Height=3,Margin=new Thickness(10,0,10,0),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(bar,2);
   bar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(0,entry.Have),GridUnitType.Star)});bar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(entry.Photos-entry.Have,0)+(entry.Photos==0?1:0),GridUnitType.Star)});
   var track=new Border{CornerRadius=new CornerRadius(1.5),Background=(Brush)panel.Resources["FillHover"]};Grid.SetColumnSpan(track,2);bar.Children.Add(track);
   if(entry.Have>0)bar.Children.Add(new Border{CornerRadius=new CornerRadius(1.5),Background=(Brush)panel.Resources[done?"Ink3":on?"Ink":"Ink3"]});
   var count=new TextBlock{Text=entry.Have+"/"+entry.Photos,FontSize=11.5,MinWidth=48,TextAlignment=TextAlignment.Right,Foreground=(Brush)panel.Resources[done?"Ink3":"Ink2"],VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(count,3);
   g.Children.Add(tick);g.Children.Add(name);g.Children.Add(bar);g.Children.Add(count);
   var row=new Button{Style=(Style)panel.FindResource("Row"),Height=34,Content=g,IsEnabled=!Generating&&!done,
    ToolTip=done?"都有 Spatial 图片了":on?"生成时包括这一项 · 点一下不包括":"生成时不包括这一项 · 点一下包括"};
   if(done)row.Opacity=1;   // nothing to choose, but not greyed out
   row.Click+=delegate{SetSpatialSkipped(entry.Entry,!SpatialSkipped(entry.Entry));RefreshGenList();GenChanged();};
   list.Children.Add(row);}
 }
 // The counts, the buttons and the menu row's note, from the generator's progress or the last count.
 void GenChanged(){
  if(panel==null)return;
  var counts=P<TextBlock>("GenCounts");var btn=P<Button>("GenBtn");var one=P<Button>("GenOneBtn");var note=P<TextBlock>("GenNote");
  string cur=currentSourcePath;bool curNeeds=cur!=null&&System.IO.File.Exists(cur)&&SpatialPathFor(cur)==null;
  if(Generating){
   P<TextBlock>("GenSize").Visibility=Visibility.Collapsed;
   counts.Text=genStuck?genPhase:genHeld!=null?"已暂停 · "+genHeld:genNotice??(genTotal>0?"正在生成 "+genProgress.Current+" / "+genTotal:genPhase);counts.ToolTip="可以关掉面板，会在后台继续";
   btn.Content=genStuck?"再试一次结束":"停止";btn.Style=(Style)panel.FindResource("Pill");btn.IsEnabled=true;one.Visibility=Visibility.Collapsed;
  }else{
   btn.Style=(Style)panel.FindResource("Primary");counts.ToolTip=genPhase!=null&&gen!=null?"上次："+genPhase:null;
   if(spatialNeeds==null){counts.Text="";btn.Content="生成 Spatial 图片";btn.IsEnabled=false;}
   else{int have=spatialNeeds.Sum(x=>x.Have),photos=spatialNeeds.Sum(x=>x.Photos),todo=spatialNeeds.Where(x=>!SpatialSkipped(x.Entry)).Sum(x=>x.Todo);
    counts.Text="已有 "+have+" / "+photos+" 张";
    btn.Content=todo==0?(spatialNeeds.Any(x=>x.Todo>0)?"没有勾选的项目":"都已生成"):"生成 Spatial 图片 · "+todo+" 张";btn.IsEnabled=todo>0&&GeneratorAvailable;
    // what it will take on disk, and what the disk has left
    var size=P<TextBlock>("GenSize");if(todo>0){string free="";try{var drive=new System.IO.DriveInfo(System.IO.Path.GetPathRoot(SpatialCacheDir));free=" · 磁盘剩余 "+SizeText(drive.AvailableFreeSpace).Replace("约 ","");}catch{}
     size.Text="预计占用"+SizeText(todo*spatialFileBytes)+free;size.Visibility=Visibility.Visible;}else size.Visibility=Visibility.Collapsed;}
   one.Visibility=curNeeds&&GeneratorAvailable?Visibility.Visible:Visibility.Collapsed;
  }
  P<Border>("GenOneGap").Visibility=one.Visibility;
  PaintGenSummary();
  btn.ToolTip=GeneratorAvailable?"在本机后台生成 · 全屏或用电池时暂停":"还没有安装 SHARP";
  if(!GeneratorAvailable){note.Text="还没有安装 SHARP";note.Visibility=Visibility.Visible;}
  if(PanelOpen&&page=="menu")RefreshSpatialNote();
 }
 // The Spatial page's row for Spatial 图片: have / all, a bar, and what is left (or what is being made).
 void PaintGenSummary(){
  var count=P<TextBlock>("GenSummaryCount");var note=P<TextBlock>("GenSummaryNote");var bar=P<Grid>("GenSummaryBar");
  int have=0,photos=0,todo=0;if(spatialNeeds!=null){have=spatialNeeds.Sum(x=>x.Have);photos=spatialNeeds.Sum(x=>x.Photos);todo=spatialNeeds.Where(x=>!SpatialSkipped(x.Entry)).Sum(x=>x.Todo);}
  if(Generating){double share=genTotal>0?Math.Min(1,(genDone+genFailed)/(double)genTotal):0;count.Text=genStuck?"":genTotal>0?genProgress.Current+" / "+genTotal:"";note.Text=genStuck?genPhase:genHeld!=null?"已暂停 · "+genHeld:genNotice??(genTotal>0?"正在生成 · 可以关掉面板，会在后台继续":genPhase);
   bar.ColumnDefinitions[0].Width=new GridLength(share,GridUnitType.Star);bar.ColumnDefinitions[1].Width=new GridLength(1-share,GridUnitType.Star);return;}
  if(!GeneratorAvailable){PaintSharpEntry(count,note,bar,have,photos);return;}   // 安装 SHARP first (SharpPage.cs)
  if(spatialNeeds==null){count.Text="";note.Text="正在统计…";return;}
  count.Text=have+" / "+photos;P<TextBlock>("StoreRowSize").Text=spatialCacheBytes>0?SizeText(spatialCacheBytes).Replace("约 ",""):"";
  note.Text=photos==0?"播放列表是空的":have==photos?"列表里的图片都有了":todo>0?todo+" 张待生成 · 预计占用"+SizeText(todo*spatialFileBytes):"还有 "+(photos-have)+" 张没有 · 都未勾选";
  bar.ColumnDefinitions[0].Width=new GridLength(Math.Max(0,have),GridUnitType.Star);bar.ColumnDefinitions[1].Width=new GridLength(Math.Max(0,photos-have)+(photos==0?1:0),GridUnitType.Star);
 }
 void GenNoteShow(string text){var n=P<TextBlock>("GenNote");n.Text=text??"";n.Visibility=string.IsNullOrEmpty(text)?Visibility.Collapsed:Visibility.Visible;}
 // The menu row: what Spatial is doing, in a few words.
 void RefreshSpatialNote(){
  string m=settings.Motion=="reverse"?"反向跟随":settings.Motion=="drift"?"自动环绕":settings.Motion=="random"?"自动手持":"跟随鼠标";
  P<TextBlock>("SpatialNote").Text=Generating?(genStuck?"生成没能完全结束":genHeld!=null?"生成已暂停":genTotal>0?"生成中 "+(genDone+genFailed)+"/"+genTotal:"生成中…"):spatialEnabled&&!SpatialPlays?"无 3D 图片":spatialEnabled?m:"";
 }
 FrameworkElement PageNamed(string name){return name=="spatial"?(FrameworkElement)P<Grid>("SpatialPage"):name=="spatialfiles"?(FrameworkElement)P<Grid>("SpatialFilesPage"):name=="spatialstore"?(FrameworkElement)P<Grid>("SpatialStorePage"):name=="sharp"?(FrameworkElement)P<Grid>("SharpPage"):name=="settings"?(FrameworkElement)P<Grid>("SettingsPage"):name=="playlist"?(FrameworkElement)P<Grid>("PlaylistPage"):P<StackPanel>("MenuPage");}

 // ------------------------------------------------------------ menu card: can this photo show in Spatial 3D?
 void RefreshSpatialBadge(string source){
  var badge=P<StackPanel>("SpatialBadge");var text=P<TextBlock>("SpatialBadgeText");var box=P<Border>("SpatialBadgeBox");
  if(source==null){badge.Visibility=Visibility.Collapsed;return;}badge.Visibility=Visibility.Visible;
  string any;bool has=spatialFiles.TryGetValue(source,out any)&&System.IO.File.Exists(any),fresh=has&&SpatialPathFor(source)!=null;
  text.Text=fresh?"3D":has?"3D 待更新":"无 3D";
  text.Foreground=(Brush)panel.Resources[fresh?"Ink":"Ink3"];box.Background=fresh?(Brush)panel.Resources["FillPress"]:Brushes.Transparent;
  // the same little chip either way: filled when it has 3D, an outline when it has not
  box.BorderBrush=fresh?(Brush)panel.Resources["FillPress"]:(Brush)panel.Resources["Edge"];
  badge.ToolTip=fresh?"这张有 Spatial 文件，可以 3D 显示":has?"图片改过，3D 文件需要更新 · Spatial 3D 页里可以生成":"这张还没有 3D 文件 · Spatial 3D 页里可以生成";
 }

 void GoPage(string to,int dir){
  if(to==page)return;var from=PageNamed(page);var target=PageNamed(to);
  if(target.Visibility==Visibility.Collapsed){var root=P<Grid>("Root");target.Height=Math.Max(0,root.ActualHeight-target.Margin.Top-target.Margin.Bottom);}
  page=to;SlidePages(from,target,dir);RefreshCube();
 }

 // ------------------------------------------------------------ settings page
 void RefreshSettings(){
  if(panel==null)return;
  string motion=settings.Motion??"follow",style=settings.PanelStyle??"ambient";
  // the camera: with the mouse (同向 / 反向) or on its own (环绕 / 随机)
  bool mouse=MotionIsMouse;var sub=P<System.Windows.Controls.Primitives.UniformGrid>("MotionSubSeg");var sb=sub.Children.OfType<Button>().ToList();
  PaintSeg(P<System.Windows.Controls.Primitives.UniformGrid>("MotionSeg"),b=>((string)b.Tag=="mouse")==mouse);
  sb[0].Content=mouse?"同向":"环绕";sb[1].Content=mouse?"反向":"手持";
  int si=motion=="reverse"||motion=="random"?1:0;PaintSeg(sub,b=>(string)b.Tag==si.ToString());
  P<TextBlock>("MotionHint").Text=mouse?(si==0?"画面朝鼠标方向倾斜":"画面朝鼠标反方向倾斜"):(si==0?"约 30 秒缓缓环绕一圈":"像手持相机一样轻轻晃动");
  PaintSeg(P<System.Windows.Controls.Primitives.UniformGrid>("StyleSeg"),b=>(string)b.Tag==style);
  PaintSeg(P<System.Windows.Controls.Primitives.UniformGrid>("BackgroundSeg"),b=>(string)b.Tag==BackgroundMode);

 }

 // ------------------------------------------------------------ 设置 page: the app itself
 void RefreshPrefs(){PaintToggle("Battery",flatOnBattery);PaintToggle("Startup",StartupOn);}
 // The same monochrome switch as Spatial's: on = primary-colour track and a tone knob, off = faint track, white knob.
 void PaintToggle(string name,bool on){
  var track=P<Border>(name+"Track");var knob=P<Ellipse>(name+"Knob");
  Color tc=((SolidColorBrush)panel.Resources[on?"PrimaryBg":(styledLight==true?"Edge":"FillHover")]).Color,kc=on?toneColor:Colors.White;
  // the knob slides and both colours fade, as the menu's Spatial switch does; the first paint is instant
  var move=knob.RenderTransform as TranslateTransform;bool first=move==null;
  if(first){knob.Margin=new Thickness(2,0,0,0);move=new TranslateTransform(on?14:0,0);knob.RenderTransform=move;track.Background=new SolidColorBrush(tc);knob.Fill=new SolidColorBrush(kc);return;}
  var tb=track.Background as SolidColorBrush;var kb=knob.Fill as SolidColorBrush;
  track.Background=Fading(tb==null?tc:tb.Color,tc,220);knob.Fill=Fading(kb==null?kc:kb.Color,kc,220);
  Animate(move,TranslateTransform.XProperty,move.X,on?14:0,260);
 }
 // Segmented choice: a thumb in the primary colour sits behind the chosen one and slides to a new choice; its words
 // take the tone as the thumb arrives, the others stay soft (brighter on hover). The thumb is added on first use.
 void PaintSeg(System.Windows.Controls.Primitives.UniformGrid g,Func<Button,bool> isOn){
  var buttons=g.Children.OfType<Button>().ToList();int idx=buttons.FindIndex(b=>isOn(b));
  var thumb=g.Tag as Border;bool first=thumb==null;
  if(first){
   var host=(Border)g.Parent;host.Child=null;var grid=new Grid();
   thumb=new Border{CornerRadius=new CornerRadius(7),HorizontalAlignment=HorizontalAlignment.Left,RenderTransform=new TranslateTransform(),IsHitTestVisible=false};
   thumb.SetResourceReference(Border.BackgroundProperty,"PrimaryBg");grid.Children.Add(thumb);grid.Children.Add(g);host.Child=grid;g.Tag=thumb;
   g.SizeChanged+=delegate{PlaceSegThumb(g,false);};
   foreach(var b in buttons){var bb=b;bb.Style=(Style)panel.FindResource("Seg");
    bb.MouseEnter+=delegate{if(bb.Uid!="on")bb.SetResourceReference(Control.ForegroundProperty,"Ink");};
    bb.MouseLeave+=delegate{if(bb.Uid!="on")bb.SetResourceReference(Control.ForegroundProperty,"Ink2");};}
  }
  int was=buttons.FindIndex(b=>b.Uid=="on");
  for(int i=0;i<buttons.Count;i++){var b=buttons[i];b.Uid=i==idx?"on":"";if(i!=idx)b.SetResourceReference(Control.ForegroundProperty,b.IsMouseOver?"Ink":"Ink2");}
  if(idx>=0){var chosen=buttons[idx];
   if(first||was<0||was==idx)chosen.SetResourceReference(Control.ForegroundProperty,"Tone");
   else{var later=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(110)};later.Tick+=delegate{later.Stop();if(chosen.Uid=="on")chosen.SetResourceReference(Control.ForegroundProperty,"Tone");};later.Start();}}
  PlaceSegThumb(g,!first&&was>=0&&was!=idx);
 }
 void PlaceSegThumb(System.Windows.Controls.Primitives.UniformGrid g,bool animate){
  var thumb=g.Tag as Border;if(thumb==null)return;var buttons=g.Children.OfType<Button>().ToList();int idx=buttons.FindIndex(b=>b.Uid=="on");
  thumb.Visibility=idx<0?Visibility.Hidden:Visibility.Visible;if(idx<0||g.ActualWidth<=0)return;
  double w=g.ActualWidth/Math.Max(1,g.Columns),x=idx*w;thumb.Width=w;thumb.Height=g.ActualHeight;
  var tr=(TranslateTransform)thumb.RenderTransform;
  if(animate)Animate(tr,TranslateTransform.XProperty,tr.X,x,240);else{tr.BeginAnimation(TranslateTransform.XProperty,null);tr.X=x;}
 }
 // On battery: the flat photo instead of 3D (SpatialPower.cs reads it; energy saver always does this).
 void SetBatteryFlat(bool flat){flatOnBattery=flat;try{Theme.AtomicWrite(BatteryFile,flat?"flat":"keep");}catch(Exception e){Log("Battery setting: "+e.Message);}UpdateSpatialPower();}
 // Start with Windows: a Run entry named "Matte" for this exe.
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 bool StartupOn{get{try{using(var k=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)){var v=k==null?null:k.GetValue("Matte") as string;return v!=null&&v.Trim('"').Equals(System.Windows.Forms.Application.ExecutablePath,StringComparison.OrdinalIgnoreCase);}}catch{return false;}}}
 string SetStartup(bool on){
  try{using(var k=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey)){if(on)k.SetValue("Matte","\""+System.Windows.Forms.Application.ExecutablePath+"\"");else k.DeleteValue("Matte",false);}return null;}
  catch(Exception e){return e.Message;}
 }
 // The timer button's label: the length, short ("30s", "5min", "1h").
 static string IntervalShort(uint ms){return ms<60000?(ms/1000)+"s":ms<3600000?(ms/60000)+"min":(ms/3600000)+"h";}
 int IntervalIndex(){int best=0;for(int i=1;i<IntervalSteps.Length;i++)if(Math.Abs((long)IntervalSteps[i]-settings.Interval)<Math.Abs((long)IntervalSteps[best]-settings.Interval))best=i;return best;}
 // The lengths, from the card's "每 5 分钟 · 下次 3:12": a short list under it, the current one ticked. Its background is
 // opaque (it floats over the desktop, outside the panel's glass), in the panel style's colours.
 void OpenIntervalPopup(){
  var popup=P<System.Windows.Controls.Primitives.Popup>("IntervalPopup");if(popup.IsOpen){popup.IsOpen=false;return;}
  bool light=styledLight==true;var box=P<Border>("IntervalPopupBox");var tint=P<Border>("IntervalPopupTint");
  var list=P<StackPanel>("IntervalList");list.Children.Clear();int current=IntervalIndex();
  for(int i=0;i<IntervalSteps.Length;i++){uint ms=IntervalSteps[i];
   var check=new ContentControl{Width=14,Height=14,Margin=new Thickness(0,0,8,0),Content=i==current?IconVisual(ICheck):null};
   // the same words and size as the timer button (30s, 5min, 1h)
   var row=new DockPanel();row.Children.Add(check);row.Children.Add(new TextBlock{Text=IntervalShort(ms),FontSize=12.5,VerticalAlignment=VerticalAlignment.Center,FontWeight=i==current?FontWeights.SemiBold:FontWeights.Normal});
   var b=new Button{Style=(Style)panel.FindResource("Row"),Height=32,Content=row,ToolTip=IntervalText(ms)};
   b.Click+=delegate{popup.IsOpen=false;SetInterval(ms);UpdateCountdown();P<TextBlock>("TimerText").Text=IntervalShort(ms);};
   list.Children.Add(b);
  }
  var src=ambBack!=null?ambBack.Source:null;
  if(SimpleStyle||src==null){
   box.Background=new SolidColorBrush(SimpleStyle?(light?Color.FromRgb(0xF9,0xF9,0xF9):Color.FromRgb(0x2B,0x2B,0x2B)):Color.FromRgb(0x33,0x2E,0x2B));tint.Background=null;P<Border>("IntervalPopupLift").Background=null;popup.IsOpen=true;return;}
  // ambient: the panel's ambience, placed where the panel shows it, so the popup is the same glass lifted off it
  var brush=new ImageBrush(src){Stretch=Stretch.Fill,ViewportUnits=BrushMappingMode.Absolute,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top};
  { var r0=P<Grid>("Root");brush.Viewport=new System.Windows.Rect(-AmbBleed-r0.ActualWidth+150,-AmbBleed-60,r0.ActualWidth+2*AmbBleed,r0.ActualHeight+2*AmbBleed); }   // about right until it is placed exactly
  box.Background=new SolidColorBrush(toneColor);   // underneath: the photo's deep tone, should the ambience not cover it
  tint.Background=brush;
  P<Border>("IntervalPopupLift").Background=new LinearGradientBrush(Color.FromArgb(0x1C,255,255,255),Color.FromArgb(0x0C,255,255,255),90);   // lighter: it sits above
  popup.IsOpen=true;
  panel.Dispatcher.BeginInvoke(new Action(()=>{
   try{var root=P<Grid>("Root");var a=root.PointToScreen(new Point(0,0));var b2=box.PointToScreen(new Point(0,0));
    var m=PresentationSource.FromVisual(root).CompositionTarget.TransformFromDevice;var d=m.Transform(new Point(a.X-b2.X,a.Y-b2.Y));
    brush.Viewport=new System.Windows.Rect(d.X-AmbBleed,d.Y-AmbBleed,root.ActualWidth+2*AmbBleed,root.ActualHeight+2*AmbBleed);}
   catch(Exception e){Log("Interval popup: "+e.Message);}
  }),System.Windows.Threading.DispatcherPriority.Loaded);
 }

 // ------------------------------------------------------------ playlist page
 int playlistGeneration;
 void RefreshPlaylist(string note){
  if(panel==null)return;
  var box=P<StackPanel>("PlaylistItems");box.Children.Clear();var entries=Playlist.ToList();
  int gen=++playlistGeneration;   // counts from an older refresh are dropped
  dragItem=null;dragging=false;int index=-1;
  foreach(string entry in entries){
   index++;bool file=System.IO.File.Exists(entry),missing=!file&&!System.IO.Directory.Exists(entry);string e=entry;
   var row=new Grid{Margin=new Thickness(0,0,0,2)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var layers=new Rectangle[3];var icon=EntryThumb(file,missing,layers);
   var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(text,1);
   text.Children.Add(new TextBlock{Text=System.IO.Path.GetFileName(entry.TrimEnd('\\'))??entry,FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=(Brush)panel.Resources[missing?"Ink3":"Ink"]});
   var sub=new TextBlock{FontSize=11.5,Foreground=(Brush)panel.Resources["Ink3"],Text=missing?"找不到了 · 不会播放":System.IO.Path.GetDirectoryName(entry)??"",ToolTip=entry};text.Children.Add(sub);
   var remove=new Button{Style=(Style)panel.FindResource("Round"),Background=Brushes.Transparent,Width=32,Height=32,ToolTip="从播放列表移除",VerticalAlignment=VerticalAlignment.Center,Cursor=System.Windows.Input.Cursors.Hand};
   remove.Content=new ContentControl{Width=15,Height=15,Content=IconVisual(IClose)};Grid.SetColumn(remove,2);
   remove.Click+=delegate{try{RefreshPlaylist(RemoveFromPlaylist(e));}catch(Exception ex){Log("Playlist remove: "+ex.Message);}};
   row.Children.Add(icon);row.Children.Add(text);row.Children.Add(remove);
   var item=new Border{CornerRadius=new CornerRadius(8),Padding=new Thickness(10,6,4,6),Background=Brushes.Transparent,Child=row,RenderTransform=new TranslateTransform(),
    ToolTip=entries.Count>1?"拖动可以调整顺序":null};
   if(entries.Count>1)item.Cursor=System.Windows.Input.Cursors.SizeNS;
   item.MouseEnter+=delegate{if(!dragging)item.Background=(Brush)panel.Resources["Fill"];};item.MouseLeave+=delegate{if(dragItem!=item)item.Background=Brushes.Transparent;};
   if(entries.Count>1)HookReorder(item,index);
   box.Children.Add(item);
   if(!missing){
    // off the UI thread: the first three images (small decodes) and, for a folder, how many it has
    Task.Run(()=>Tuple.Create(file?1:CountImages(e),FirstImages(e,3).Select(EntryImage).Where(b=>b!=null).ToList())).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
     if(gen!=playlistGeneration||t.Exception!=null)return;
     if(!file)sub.Text=System.IO.Path.GetDirectoryName(e)+" · "+t.Result.Item1+" 张";
     FillThumb(layers,t.Result.Item2);
    })));
   }
  }
  if(entries.Count==0){   // empty state
   var empty=new StackPanel{Margin=new Thickness(0,48,0,0),HorizontalAlignment=HorizontalAlignment.Center};
   empty.Children.Add(new ContentControl{Width=34,Height=34,Content=IconVisual(IList),Foreground=(Brush)panel.Resources["Ink3"],Margin=new Thickness(0,0,0,12)});
   empty.Children.Add(new TextBlock{Text="播放列表是空的",FontSize=14,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center});
   empty.Children.Add(new TextBlock{Text="添加文件夹（含子文件夹）或单张图片，\nMatte 就会开始轮播",FontSize=12,Foreground=(Brush)panel.Resources["Ink2"],TextAlignment=TextAlignment.Center,TextTrimming=TextTrimming.None,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,6,0,0)});
   box.Children.Add(empty);
  }
  P<TextBlock>("PlaylistCount").Text=entries.Count+" 项";
  Task.Run(()=>SourceImages().Count).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{if(gen==playlistGeneration&&t.Exception==null)P<TextBlock>("PlaylistCount").Text=entries.Count+" 项 · 共 "+t.Result+" 张";})));
  var noteBlock=P<TextBlock>("PlaylistNote");noteBlock.Text=note??"";noteBlock.Visibility=note==null?Visibility.Collapsed:Visibility.Visible;
 }
 // ---- drag to reorder: press on a row and move it up or down; the others slide out of its way; let go to drop it there.
 // Every row is the same height, so the slot under the dragged row is its start plus the rows moved over.
 Border dragItem;int dragFrom,dragTo;double dragStartY;bool dragging;
 void HookReorder(Border item,int index){
  item.PreviewMouseLeftButtonDown+=(s,e)=>{
   for(var d=e.OriginalSource as DependencyObject;d!=null&&d!=item;d=VisualTreeHelper.GetParent(d))if(d is Button)return;   // the × stays a button
   dragItem=item;dragFrom=dragTo=index;dragStartY=e.GetPosition(P<StackPanel>("PlaylistItems")).Y;dragging=false;item.CaptureMouse();e.Handled=true;};
  item.MouseMove+=(s,e)=>{
   if(dragItem!=item||!item.IsMouseCaptured)return;
   var box=P<StackPanel>("PlaylistItems");double dy=e.GetPosition(box).Y-dragStartY;
   if(!dragging){if(Math.Abs(dy)<4)return;dragging=true;Panel.SetZIndex(item,10);item.Background=(Brush)panel.Resources["FillPress"];
    item.Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=14,ShadowDepth=3,Direction=270,Opacity=.3};item.Cursor=System.Windows.Input.Cursors.SizeNS;}
   int n=box.Children.Count;double h=item.ActualHeight;
   dy=Math.Max(-dragFrom*h,Math.Min((n-1-dragFrom)*h,dy));   // not past the ends
   ((TranslateTransform)item.RenderTransform).Y=dy;
   int to=Math.Max(0,Math.Min(n-1,dragFrom+(int)Math.Round(dy/h)));
   if(to==dragTo)return;dragTo=to;
   for(int i=0;i<n;i++){if(i==dragFrom)continue;var other=(FrameworkElement)box.Children[i];
    double shift=dragFrom<to&&i>dragFrom&&i<=to?-h:dragFrom>to&&i<dragFrom&&i>=to?h:0;
    Animate((TranslateTransform)other.RenderTransform,TranslateTransform.YProperty,((TranslateTransform)other.RenderTransform).Y,shift,160);}
  };
  item.MouseLeftButtonUp+=(s,e)=>{
   // (releasing the capture raises LostMouseCapture at once: the drag is taken over first, so that handler, which
   // cancels a drag cut short, leaves this one alone)
   if(dragItem!=item)return;bool wasDragging=dragging;dragItem=null;dragging=false;item.ReleaseMouseCapture();
   if(!wasDragging)return;
   int from=dragFrom,to=dragTo;double h=item.ActualHeight;
   var t=(TranslateTransform)item.RenderTransform;var a=new System.Windows.Media.Animation.DoubleAnimation(t.Y,(to-from)*h,TimeSpan.FromMilliseconds(140)){EasingFunction=new System.Windows.Media.Animation.QuadraticEase()};
   System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(a,UiFrameRate);
   a.Completed+=delegate{if(from!=to)MovePlaylistEntry(from,to);RefreshPlaylist(null);};
   t.BeginAnimation(TranslateTransform.YProperty,a);
  };
  item.LostMouseCapture+=(s,e)=>{if(dragItem==item&&dragging){dragItem=null;dragging=false;RefreshPlaylist(null);}};   // e.g. the panel closed mid-drag: nothing moves
 }
 // An entry's thumbnail: up to three rounded squares stacked vertically, like an album. The front one is the first image
 // (a single image, or a folder's first by name); the folder's next two sit behind it, each a little narrower, peeking
 // above it, so a folder reads as a stack. The cards are solid and edged with a light hairline, which keeps them
 // apart without shading them darker. Until they
 // load (and for a missing entry) the front square shows the folder / image icon.
 const double ThumbSize=48,ThumbStep=6,ThumbInset=4;
 FrameworkElement EntryThumb(bool file,bool missing,Rectangle[] layers){
  var canvas=new Canvas{Width=ThumbSize,Height=ThumbSize+2*ThumbStep,Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Center};
  for(int i=2;i>=0;i--){   // back to front
   double w=ThumbSize-2*ThumbInset*i,h=ThumbSize-2*ThumbInset*i;
   // each card edged with a light hairline (as the menu card's stack): no shading, no shadow
   var r=new Rectangle{Width=w,Height=h,RadiusX=8,RadiusY=8,Fill=(Brush)panel.Resources["Fill"],Stroke=new SolidColorBrush(Color.FromArgb(0x8C,255,255,255)),StrokeThickness=.8,
    Visibility=i==0?Visibility.Visible:Visibility.Hidden};
   var shade=new Rectangle{Width=w,Height=h,IsHitTestVisible=false,Visibility=Visibility.Hidden};   // (kept so FillThumb's pairs stay; draws nothing)
   r.Tag=shade;
   // horizontally centred; each one behind shows ThumbStep above the one in front of it
   foreach(var x in new[]{r,shade}){Canvas.SetLeft(x,ThumbInset*i);Canvas.SetTop(x,(2-i)*ThumbStep);canvas.Children.Add(x);}layers[i]=r;
  }
  const double I=18;var icon=new ContentControl{Width=I,Height=I,Content=IconVisual(file?IImage:IFolder),Foreground=(Brush)panel.Resources[missing?"Ink3":"Ink2"],IsHitTestVisible=false};
  Canvas.SetLeft(icon,(ThumbSize-I)/2);Canvas.SetTop(icon,2*ThumbStep+(ThumbSize-I)/2);canvas.Children.Add(icon);
  return canvas;
 }
 void FillThumb(Rectangle[] layers,List<BitmapSource> images){
  if(images.Count==0)return;
  for(int i=0;i<3;i++){
   var shade=(Rectangle)layers[i].Tag;
   if(i>=images.Count){layers[i].Visibility=shade.Visibility=Visibility.Hidden;continue;}
   layers[i].Fill=new ImageBrush(images[i]){Stretch=Stretch.UniformToFill};layers[i].Visibility=Visibility.Visible;shade.Visibility=i>0?Visibility.Visible:Visibility.Hidden;
  }
  var canvas=(Canvas)layers[0].Parent;foreach(var c in canvas.Children.OfType<ContentControl>().ToList())canvas.Children.Remove(c);   // the icon placeholder goes
 }
 // The first n images of an entry: the image itself, or a folder's by file name, then its subfolders' (by name).
 static List<string> FirstImages(string entry,int n){
  var found=new List<string>();if(System.IO.File.Exists(entry)){found.Add(entry);return found;}
  var queue=new Queue<string>();queue.Enqueue(entry);
  while(queue.Count>0&&found.Count<n){string d=queue.Dequeue();
   try{foreach(string f in System.IO.Directory.GetFiles(d).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))if(IsImage(f)){found.Add(f);if(found.Count>=n)break;}
    foreach(string s in System.IO.Directory.GetDirectories(d).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))queue.Enqueue(s);}
   catch{}}
  return found;
 }
 // A small square-ish decode (96 px wide) for a thumbnail; null if the file can't be read.
 static BitmapSource EntryImage(string path){
  try{var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.CreateOptions=BitmapCreateOptions.IgnoreColorProfile;b.DecodePixelWidth=160;b.UriSource=new Uri(path);b.EndInit();b.Freeze();return UprightWpf(b,path);}
  catch{return null;}
 }
 // A system dialog owned by the panel; the panel stays open behind it (dropping: deactivation does not hide it).
 void AddPlaylistItems(bool folders){
  var h=new WindowInteropHelper(panel).Handle;List<string> picked;
  dropping=true;
  try{picked=PickPaths(h,folders);}
  catch(Exception e){Log("Playlist picker: "+e.Message);picked=new List<string>();}
  finally{dropping=false;}
  panel.Activate();
  if(picked.Count==0)return;
  RefreshPlaylist(AddToPlaylist(picked));
 }
 FrameworkElement IconVisual(string data){
  var p=new Path{Data=Geometry.Parse(data),StrokeThickness=2.4,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
  p.SetBinding(Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Control),1)});
  var canvas=new Canvas{Width=24,Height=24};canvas.Children.Add(p);var vb=new Viewbox{Child=canvas};EvenStroke(vb,p);return vb;
 }

 // ------------------------------------------------------------ panel styles
 // ambient: the photo's blurred ambience (Backdrop.cs) with light text. simple: a plain surface in the system theme's
 // colours (light or dark, read each time the panel opens), no photo behind it.
 bool SimpleStyle{get{return settings!=null&&settings.PanelStyle=="simple";}}
 static bool SystemLight(){
  try{using(var k=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))return k!=null&&Convert.ToInt32(k.GetValue("AppsUseLightTheme",0))==1;}
  catch{return false;}
 }
 static SolidColorBrush Argb(uint v){var b=new SolidColorBrush(Color.FromArgb((byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v));b.Freeze();return b;}
 bool? styledLight;bool? styledSimple;
 void ApplyPanelStyle(){
  if(panel==null)return;bool simple=SimpleStyle,light=simple&&SystemLight();
  if(styledSimple==simple&&styledLight==light)return;styledSimple=simple;styledLight=light;
  var R=panel.Resources;
  // ink, ink2, ink3, fill, fill hover, fill press, hair, edge, danger, primary, primary hover
  uint[] c=light?new uint[]{0xFF1B1B1B,0xA6000000,0x73000000,0x0F000000,0x1A000000,0x26000000,0x14000000,0x33000000,0xFFC42B1C,0xFF1B1B1B,0xFF3A3A3A}
                 :new uint[]{0xFFFFFFFF,0xB8FFFFFF,0x7AFFFFFF,0x1CFFFFFF,0x2EFFFFFF,0x42FFFFFF,0x24FFFFFF,0x59FFFFFF,0xFFFFB0B0,0xFFFFFFFF,0xFFEFEDEB};
  string[] keys={"Ink","Ink2","Ink3","Fill","FillHover","FillPress","Hair","Edge","Danger","PrimaryBg","PrimaryHover"};
  for(int i=0;i<keys.Length;i++)R[keys[i]]=Argb(c[i]);
  var root=P<Grid>("Root");var show=simple?Visibility.Collapsed:Visibility.Visible;
  if(ambLayer!=null){ambLayer.Visibility=show;ambDim.Visibility=show;ambSheen.Visibility=show;}
  root.Background=new SolidColorBrush(simple?(light?Color.FromRgb(0xF9,0xF9,0xF9):Color.FromRgb(0x2B,0x2B,0x2B)):Color.FromRgb(0x2A,0x25,0x22));
  if(simple){toneColor=light?Colors.White:Color.FromRgb(0x1B,0x1B,0x1B);toneBrush=new SolidColorBrush(toneColor);R["Tone"]=toneBrush;}
  else{ambPath=null;string shown=currentSourcePath==null?null:DisplayPath(currentSourcePath);SetAmbient(shown,0);}   // the ambience and its tone come back
  try{var h=new WindowInteropHelper(panel).Handle;if(h!=IntPtr.Zero){int v=light?0:1;DwmSetWindowAttribute(h,20,ref v,4);}}catch{}
  switchOn=null;SetSwitch(spatialEnabled);
  if(PanelOpen)RefreshPanel();
 }
}
