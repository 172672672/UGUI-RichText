/* 
 * -----------------------------------------------
 * Copyright (c) 172672672 All rights reserved.
 * -----------------------------------------------
 * 
 * 本脚本实现参考github项目
 * https://github.com/SylarLi/RichText
 * 还有一波(https://github.com/coding2233/TextInlineSprite)
 * 
 * 修改：
 * 纹理设置走外部函数
 * 添加纹理上下对齐方式
 * 纹理支持动画
 * 占位符不用空格（空格会导致换行）
 * 显示不全时顶点数据可能出错
 * 
 * 测试文本：
 * 图片<material=image sprite=face1+face2+face3+face4 atlas=storyFace frame=5 w=50 h=50 pivot=0 /></material><material=underline event=abc args=123 c=#ff3300 >下划线下划线</material><material=shadow>阴影阴影</material><material=outline>描边描边</material><material=outline><material=gradient from=#ff0000 to=#00ff00 x=0 y=1>颜色渐变+描边</material></material><material=shadow><color=#ff0000>支持<size=16>默认标签</size></color></material>
 * 
 * 测试版本：Unity 5.3.6f1
 * 
 * Coder：Quan
 * Time ：2017.04.13
*/

using System;
using UnityEngine;
using System.Text;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// 图片<material=image sprite=???+??? atlas=??? frame=20 w=1 h=1 pivot=1 event=*** args=***/></material>
/// 阴影<material=shadow c=#000000 x=1 y=-1>blablabla...</material>
/// 描边<material=outline c=#000000 x=1 y=-1>blablabla...</material>
/// 渐变<material=gradient from=#FFFFFF to=#000000 x=0 y=-1>blablabla...</material>
/// 下划线<material=underline c=#FFFFFF h=1.5 event=*** args=***>blablabla...</material>
/// 只有下划线会受到Text.color的影响
/// 支持Unity RichText标签: color, size, b, i
/// 标签可嵌套
/// unity会忽略material对排版的影响，这里全部使用material标签以简化标签检测
/// </summary>
[ExecuteInEditMode]
public class ZUIRichText : Text, IPointerClickHandler
{
    private const char replaceChar = 'i';

    private FontData fontData;
    private UIVertex[] tempVerts;
    private Action<string, string> clickHandler = delegate { };
    public Action<GameObject> clickEvent = delegate { };  //在不响应点击超链接的点击事件
    private TextInterpreter textInterpreter = new TextInterpreter();

    // --------------- img ---------------- //
    private static readonly Regex IconReg = new Regex(@"<material=image ([^>\s]+)([^>]*)></material>");//new Regex(@"<img ([^>\s]+)([^>]*)/>");
    private static readonly Regex ItemReg = new Regex(@"(\w+)=([^\s]+)");

    private class IconInfo
    {
        public string[] sprite;
        public string atlas;
        public float frame;
        public float pivot;
        //public bool still;
        public Color color;
        public Vector2 position;
        public Vector2 size;
        public int vertice;
        public int vlength;
        public string evt;
        public string args;
    }

    private List<ZUIFrameAnimation> imagePool = new List<ZUIFrameAnimation>();
    private List<IconInfo> icons = new List<IconInfo>();
    private List<Event> eventList = new List<Event>();
    private bool imageDirty;

    // --------------- Event -------------- //
    private class Event
    {
        public Rect rect;
        public string name;
        public string args;
    }

    private Action<ZUIImage, string, string> imgSpriteSetter;
    private Func<string, string, Sprite> imgSpriteGetter;
    private string preferTextWid;
    private string preferTextHei;
    private bool vertsDirty;
    private Action onReLayout;

    protected ZUIRichText()
    {
        fontData = typeof(Text).GetField("m_FontData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(this) as FontData;
        tempVerts = typeof(Text).GetField("m_TempVerts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(this) as UIVertex[];
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        imagePool = null;
        clickHandler = null;
    }

    public override void SetVerticesDirty()
    {
        base.SetVerticesDirty();
        vertsDirty = true;
    }

    /// <summary>
    /// 添加点击回调
    /// </summary>
    public void AddListener(Action<string, string> callBack)
    {
        clickHandler += callBack;
    }

    /// <summary>
    /// 清空回调
    /// 注：之前有的地方会反复调用AddListener，结果委托链造成同时弹出多个框，因此尽量只在初始注册事件，或者注册前先清空回调
    /// </summary>
    public void ClearListener()
    {
        clickHandler = delegate { };
    }

    /// <summary>
    /// 一次性事件，富文本第一次正确显示时回调（包括宽度高度等）
    /// </summary>
    /// <param name="a"></param>
    public void SetOnRelayout(Action a)
    {
        onReLayout = a;
    }

    public void RemoveAllListeners()
    {
        clickHandler = delegate { };
    }

    /// <summary>
    /// 设置回调
    /// image， atlas, spriteName
    /// </summary>
    public void SetImageSetter(Action<ZUIImage, string, string> func)
    {
        imgSpriteSetter = func;
    }

    /// <summary>
    /// 设置回调
    /// atlas, spriteName, sprite
    /// </summary>
    public void SetSpriteGetter(Func<string, string, Sprite> func)
    {
        imgSpriteGetter = func;
    }

    protected void SetImageDirty()
    {
        imageDirty = true;
    }

    protected override void OnPopulateMesh(VertexHelper toFill)
    {
        vertsDirty = false;
        //不走富文本
        if(!supportRichText)
        {
            base.OnPopulateMesh(toFill);
            icons.Clear();
            SetImageDirty();
            return;
        }

        if(font == null)
            return;
        // We don't care if we the font Texture changes while we are doing our Update.
        // The end result of cachedTextGenerator will be valid for this instance.
        // Otherwise we can get issues like Case 619238.
        m_DisableFontTextureRebuiltCallback = true;

        eventList.Clear();
        IList<UIVertex> verts = null;

        // Caculate layout
        string viewText = CalculateLayoutOfImage(text, out verts);

        // Last 4 verts are always a new line...
        int vertCount = verts.Count - 4;

        // Parse color tag
        List<Tag> tags = null;
        textInterpreter.Parse(viewText, out tags);

        // Apply tag effect
        if(tags.Count > 0)
        {
            List<UIVertex> vertexs = verts as List<UIVertex>;
            if(vertexs != null)
            {
                int capacity = 0;
                for(int i = 0, len = tags.Count; i < len; i++)
                {
                    Tag tag = tags[i];
                    switch(tag.type)
                    {
                        case TagType.Shadow:
                            capacity += (tag.end - tag.start) * 4;
                            break;

                        case TagType.Outline:
                            capacity += (tag.end - tag.start) * 4 * 5;
                            break;
                    }
                }
                if(capacity > 0)
                {
                    capacity = Mathf.Max(capacity, 16);
                    vertexs.Capacity += capacity;
                }
            }
            for(int i = 0, len = tags.Count; i < len; i++)
            {
                Tag tag = tags[i];
                switch(tag.type)
                {
                    case TagType.Shadow:
                    ApplyShadowEffect(tag as Shadow, verts, vertCount);
                    break;

                    case TagType.Outline:
                    ApplyOutlineEffect(tag as Outline, verts, vertCount);
                    break;

                    case TagType.Gradient:
                    ApplyGradientEffect(tag as GradientL, verts, vertCount);
                    break;

                    case TagType.Underline:
                    ApplyUnderlineEffect(tag as Underline, verts, vertCount);
                    break;
                }
            }
        }
        
        vertCount = verts.Count;
        Rect inputRect = rectTransform.rect;
        float unitsPerPixel = 1f / pixelsPerUnit;
        // get the text alignment anchor point for the text in local space
        Vector2 textAnchorPivot = GetTextAnchorPivot(fontData.alignment);
        Vector2 refPoint = Vector2.zero;
        refPoint.x = (textAnchorPivot.x == 1 ? inputRect.xMax : inputRect.xMin);
        refPoint.y = (textAnchorPivot.y == 0 ? inputRect.yMin : inputRect.yMax);

        // Determine fraction of pixel to offset text mesh.
        Vector2 roundingOffset = PixelAdjustPoint(refPoint) - refPoint;

        toFill.Clear();
        if(roundingOffset != Vector2.zero)
        {
            for(int i = 0; i < vertCount; ++i)
            {
                int tempVertsIndex = i & 3;
                tempVerts[tempVertsIndex] = verts[i];
                tempVerts[tempVertsIndex].position *= unitsPerPixel;
                tempVerts[tempVertsIndex].position.x += roundingOffset.x;
                tempVerts[tempVertsIndex].position.y += roundingOffset.y;
                if(tempVertsIndex == 3)
                    toFill.AddUIVertexQuad(tempVerts);
            }
        } else
        {
            for(int i = 0; i < vertCount; ++i)
            {
                int tempVertsIndex = i & 3;
                tempVerts[tempVertsIndex] = verts[i];
                tempVerts[tempVertsIndex].position *= unitsPerPixel;
                if(tempVertsIndex == 3)
                    toFill.AddUIVertexQuad(tempVerts);
            }
        }
        m_DisableFontTextureRebuiltCallback = false;
        verts.Clear();
    }

    /// <summary>
    /// 纹理
    /// </summary>
    protected string CalculateLayoutOfImage(string richText, out IList<UIVertex> verts)
    {
        float fontSize2 = fontSize * 0.5f;
        float unitsPerPixel = 1f / pixelsPerUnit;

        Vector2 extents = rectTransform.rect.size;
        var settings = GetGenerationSettings(extents);
        settings.verticalOverflow = VerticalWrapMode.Overflow;

        // Image replace
        icons.Clear();
        Match match = null;
        StringBuilder builder = new StringBuilder();
        while((match = IconReg.Match(richText)).Success)
        {
            IconInfo iconInfo = new IconInfo();
            float w = fontSize2, h = fontSize2, pivot = 0f, frame = -1f;
            string[] sprites = null;
            string atlas = null, e = null, args = null;
            Match itemMatch = ItemReg.Match(match.Value);
            while(itemMatch.Success)
            {
                string name = itemMatch.Groups[1].Value;
                string value = itemMatch.Groups[2].Value;
                switch(name)
                {
                    case "frame":   //帧率
                    float.TryParse(value, out frame);
                    break;

                    case "sprite":  //纹理
                    sprites = value.Split('+');
                    break;

                    case "atlas": //图集
                    atlas = value;
                    break;

                    case "pivot":   //y方向锚点
                    float.TryParse(value, out pivot);
                    break;

                    case "w":   //宽
                    float.TryParse(value, out w);
                    break;

                    case "h":   //高
                    float.TryParse(value, out h);
                    break;

                    case "event":   //事件
                    e = value;
                    break;

                    case "args":    //事件参数
                    args = value;
                    break;
                }
                itemMatch = itemMatch.NextMatch();
            }

            iconInfo.size = new Vector2(w, h);
            iconInfo.pivot = pivot;
            iconInfo.frame = frame;
            iconInfo.atlas = atlas;
            iconInfo.sprite = sprites;
            iconInfo.evt = e;
            iconInfo.args = args;
            iconInfo.vertice = match.Index * 4;
            icons.Add(iconInfo);
            float size = (1f - pivot) * (iconInfo.size.y - fontSize) + fontSize;
            string replace  = string.Format("<size={0}>{1}</size>", size, replaceChar);
            float spaceWidth = cachedTextGenerator.GetPreferredWidth(replace, settings) * unitsPerPixel;
            int holderLen = Mathf.CeilToInt(iconInfo.size.x / spaceWidth);
            if(holderLen < 0)
                holderLen = 0;

            //占位符
            builder.Length = 0;
            builder.Append(string.Format("<color=#00000000><size={0}>", size));
            builder.Append(replaceChar, holderLen);
            builder.Append("</size></color>");
            string holder = builder.ToString();
            iconInfo.vlength = holder.Length * 4;

            //截取
            builder.Length = 0;
            builder.Append(richText, 0, match.Index);
            builder.Append(holder);
            builder.Append(richText, match.Index + match.Length, richText.Length - match.Index - match.Length);
            richText = builder.ToString();
        }

        // Populate characters
        cachedTextGenerator.Populate(richText, settings);
        
        //这里不是直接给verts赋值，而是返回一个副本，避免操作unity内部verts造成顶点泄漏
        UIVertex[] ary = new UIVertex[cachedTextGenerator.verts.Count];
        cachedTextGenerator.verts.CopyTo(ary, 0);
        verts = new List<UIVertex>(ary);
        
        // Last 4 verts are always a new line...
        int vertCount = verts.Count - 4;
        if(vertCount <= 0)
        {
            icons.Clear();
            SetImageDirty();
            preferTextWid = preferTextHei = richText;
            return richText;
        }

        preferTextWid = richText;
        // Image wrap check
        if(horizontalOverflow == HorizontalWrapMode.Wrap)
        {
            //水平自动换行
            for(int i = 0; i < icons.Count; i++)
            {
                IconInfo iconInfo = icons[i];
                int vertice = iconInfo.vertice;
                int vlength = iconInfo.vlength;
                if(verts[vertice + vlength - 1].position.x < verts[vertice].position.x)
                {
                    // New line
                    richText = richText.Insert(vertice / 4, "\r\n");
                    for(int j = i; j < icons.Count; j++)
                        icons[j].vertice += 8;
                    //todo:这里也要小心
                    cachedTextGenerator.Populate(richText, settings);
                    verts = cachedTextGenerator.verts;
                    vertCount = verts.Count - 4;
                }
            }
        }
        if(verticalOverflow == VerticalWrapMode.Truncate)
        {
            //溢出框外的不要
            float minY = rectTransform.rect.yMin;
            while(vertCount > 0 && verts[vertCount - 2].position.y * unitsPerPixel < minY)
            {
                verts.RemoveAt(vertCount - 1);
                verts.RemoveAt(vertCount - 2);
                verts.RemoveAt(vertCount - 3);
                verts.RemoveAt(vertCount - 4);
                vertCount -= 4;
            }
        }
        preferTextHei = richText;

        // Image position calculation
        for(int i = icons.Count - 1; i >= 0; i--)
        {
            IconInfo iconInfo = icons[i];
            int vertice = iconInfo.vertice;
            if(vertice < vertCount)
            {
                UIVertex vert = verts[vertice];
                Vector2 vertex = vert.position;
                vertex *= unitsPerPixel;
                vertex += new Vector2(iconInfo.size.x * 0.5f, (fontSize2 + iconInfo.size.y * (0.5f - iconInfo.pivot)) * 0.5f);
                vertex += new Vector2(rectTransform.sizeDelta.x * (rectTransform.pivot.x - 0.5f), rectTransform.sizeDelta.y * (rectTransform.pivot.y - 0.5f));
                iconInfo.position = vertex;
                iconInfo.color = Color.white;

                if(!string.IsNullOrEmpty(iconInfo.evt))
                {
                    Event e = new Event();
                    e.name = iconInfo.evt;
                    e.args = iconInfo.args;
                    e.rect = new Rect(verts[vertice].position.x * unitsPerPixel, verts[vertice].position.y * unitsPerPixel + (fontSize2 - iconInfo.size.y) * 0.5f, iconInfo.size.x, iconInfo.size.y);
                    eventList.Add(e);
                }
            } else
            {
                icons.RemoveAt(i);
            }
        }

        // Need re-layout image
        SetImageDirty();
        return richText;
    }

    protected void Update()
    {
        if(imageDirty)
        {
            imageDirty = false;
            imagePool.RemoveAll(image => image == null);
            if(imagePool.Count == 0) GetComponentsInChildren<ZUIFrameAnimation>(true, imagePool);
            for(int i = imagePool.Count, len = icons.Count; i < len; i++)
                imagePool.Add(NewImage());

            for(int i = 0; i < icons.Count; i++)
            {
                var color = icons[i].color;
                if(color.a <= 0)
                    continue;
                var position = icons[i].position;
                var size = icons[i].size;
                //var still = icons[i].still;
                var img = imagePool[i];
                if(icons[i].sprite != null)
                {
                    //纹理
                    if(icons[i].frame > 0)
                    {
                        //动画
                        if(imgSpriteGetter != null)
                            img.Initialize(icons[i].sprite, icons[i].atlas, imgSpriteGetter, icons[i].frame);
                        else if(imgSpriteSetter != null)
                            img.Initialize(icons[i].sprite, icons[i].atlas, imgSpriteSetter, icons[i].frame);
                    } else
                    {
                        //静态
                        if(imgSpriteGetter != null)
                            img.sprite = imgSpriteGetter(icons[i].atlas, icons[i].sprite[0]);
                        else if(imgSpriteSetter != null)
                            imgSpriteSetter(img, icons[i].atlas, icons[i].sprite[0]);
                    }
                }
                img.color = color;
                img.rectTransform.sizeDelta = size;
                img.rectTransform.anchoredPosition = position;
                imagePool[i].gameObject.SetActive(true);
            }
            for(int i = icons.Count; i < imagePool.Count; i++)
            {
                imagePool[i].sprite = null;
                imagePool[i].gameObject.SetActive(false);
            }
            
            //如果第一次有效重绘且有初始化事件则调用
            if (onReLayout != null)
            {
                onReLayout();
                onReLayout = null;
            }
        }
    }

    private ZUIFrameAnimation NewImage()
    {
        GameObject obj = new GameObject("img");
        ZUIFrameAnimation img = obj.AddComponent<ZUIFrameAnimation>();
        obj.layer = gameObject.layer;
        img.raycastTarget = false;
        var rt = obj.transform as RectTransform;
        if(rt)
            rt.SetParent(rectTransform, false);
        return img;
    }

    /// <summary>
    /// 投影效果
    /// </summary>
    private void ApplyShadowEffect(Shadow tag, IList<UIVertex> verts, int vertCount)
    {
        int start = tag.start * 4;
        int end = Mathf.Min(tag.end * 4 + 4, vertCount);
        UIVertex vt;
        for(int i = start; i < end; i++)
        {
            vt = verts[i];
            verts.Add(vt);
            Vector3 v = vt.position;
            v.x += tag.x;
            v.y += tag.y;
            vt.position = v;
            var newColor = tag.c;
            newColor.a = (newColor.a * verts[i].color.a) / 255f;
            vt.color = newColor;
            verts[i] = vt;
        }
    }

    /// <summary>
    /// 描边效果
    /// </summary>
    private void ApplyOutlineEffect(Outline tag, IList<UIVertex> verts, int vertCount)
    {
        int start = tag.start * 4;
        int end = Mathf.Min(tag.end * 4 + 4, vertCount);
        UIVertex vt;
        for(int x = -1; x <= 1; x += 2)
        {
            for(int y = -1; y <= 1; y += 2)
            {
                for(int i = start; i < end; i++)
                {
                    vt = verts[i];
                    Vector3 v = vt.position;
                    v.x += tag.x * x;
                    v.y += tag.y * y;
                    vt.position = v;
                    var newColor = tag.c;
                    newColor.a = (newColor.a * verts[i].color.a) / 255f;
                    vt.color = newColor;
                    verts.Add(vt);
                }
            }
        }
        for(int i = start; i < end; i++)
            verts.Add(verts[i]);
    }

    /// <summary>
    /// 渐变效果
    /// </summary>
    private void ApplyGradientEffect(GradientL tag, IList<UIVertex> verts, int vertCount)
    {
        int start = tag.start * 4;
        int end = Mathf.Min(tag.end * 4 + 4, vertCount);

        float min = float.MaxValue;
        float max = float.MinValue;
        Vector2 dir = new Vector2(tag.x, tag.y);
        for(int i = start; i < end; i++)
        {
            float dot = Vector3.Dot(verts[i].position, dir);
            if(dot > max) max = dot;
            else if(dot < min) min = dot;
        }

        UIVertex vt;
        float h = max - min;
        for(int i = start; i < end; i++)
        {
            vt = verts[i];
            Vector3 pos = vt.position;
            vt.color = Color32.Lerp(tag.from, tag.to, (Vector3.Dot(pos, dir) - min) / h);
            verts[i] = vt;
        }
    }

    /// <summary>
    /// 下划线
    /// </summary>
    private void ApplyUnderlineEffect(Underline tag, IList<UIVertex> verts, int vertCount)
    {
        int start = tag.start * 4;
        int end = Mathf.Min(tag.end * 4 + 4, vertCount);
        if(verts.Count <= start + 3)
            return;

        float fontSize2 = fontSize * 0.5f;
        float unitsPerPixel = 1 / pixelsPerUnit;

        UIVertex vt1 = verts[start + 3];
        UIVertex vt2;
        float minY = vt1.position.y;
        for(int i = start + 2; i <= end - 2; i += 4)
        {
            vt2 = verts[i];
            bool newline = Mathf.Abs(vt2.position.y - vt1.position.y) > fontSize2;
            if(newline || i == end - 2)
            {
                IconInfo iconInfo = new IconInfo();
                //iconInfo.still = true;
                int tailIndex = !newline && i == end - 2 ? i : i - 4;
                vt2 = verts[tailIndex];
                minY = Mathf.Min(minY, vt2.position.y);
                iconInfo.size = new Vector2((vt2.position.x - vt1.position.x) * unitsPerPixel, tag.h);
                Vector2 vertex = new Vector2(vt1.position.x, minY);
                vertex *= unitsPerPixel;
                vertex += new Vector2(iconInfo.size.x * 0.5f, -tag.h * 0.5f);
                vertex += new Vector2(rectTransform.sizeDelta.x * (rectTransform.pivot.x - 0.5f), rectTransform.sizeDelta.y * (rectTransform.pivot.y - 0.5f));
                iconInfo.position = vertex;
                iconInfo.color = tag.c == Color.white ? color : tag.c;
                icons.Add(iconInfo);

                if(!string.IsNullOrEmpty(tag.e))
                {
                    Event e = new Event();
                    e.name = tag.e;
                    e.args = tag.args;
                    e.rect = new Rect(vt1.position.x * unitsPerPixel, minY * unitsPerPixel, iconInfo.size.x, (verts[tailIndex - 1].position.y - minY) * unitsPerPixel);
                    eventList.Add(e);
                }

                vt1 = verts[i + 1];
                minY = vt1.position.y;
                if(newline && i == end - 2) i -= 4;
            } else
            {
                minY = Mathf.Min(minY, vt2.position.y);
            }
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventList.Count == 0)
        {
            if (clickEvent != null)
                clickEvent.Invoke(eventData.selectedObject);
            return;
        }
        Vector2 lp;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, eventData.position, eventData.pressEventCamera, out lp);
        for(int i = eventList.Count - 1; i >= 0; i--)
        {
            Event e = eventList[i];
            if(e.rect.Contains(lp))
            {
                clickHandler.Invoke(e.name, e.args);
                return;
            }
        }
        if (clickEvent != null)
           clickEvent.Invoke(eventData.selectedObject);
    }

    public float GetPreferredHeight(float targetWidth)
    {
        string sizeText = text;
        if (supportRichText)
        {
            if (vertsDirty)
            {
                vertsDirty = false;
                IList<UIVertex> list;
                CalculateLayoutOfImage(text, out list);
            }
            if (!string.IsNullOrEmpty(preferTextHei))
                sizeText = preferTextHei;
        }
        var settings = GetGenerationSettings(new Vector2(targetWidth, 0f));
        return cachedTextGeneratorForLayout.GetPreferredHeight(sizeText, settings) / pixelsPerUnit;
    }

    public override float preferredHeight
    {
        get{
            return GetPreferredHeight(rectTransform.rect.size.x);
        }
    }

    public override float preferredWidth
    {
        get{
            string sizeText = text;
            if(supportRichText)
            {
                if(vertsDirty)
                {
                    vertsDirty = false;
                    IList<UIVertex> list;
                    CalculateLayoutOfImage(text, out list);
                }
                if(!string.IsNullOrEmpty(preferTextWid))
                    sizeText = preferTextWid;
            }
            var settings = GetGenerationSettings(Vector2.zero);
            return cachedTextGeneratorForLayout.GetPreferredWidth(sizeText, settings) / pixelsPerUnit;
        }
    }

    private class TextInterpreter
    {
        private static readonly Regex TagReg = new Regex(@"</*material[^>]*>");
        private const string TagSuffix = "</material>";

        private List<Tag> close;
        private Stack<InterpretInfo> open;

        public TextInterpreter()
        {
            close = new List<Tag>();
            open = new Stack<InterpretInfo>();
        }

        public void Parse(string richText, out List<Tag> tags)
        {
            close.Clear();
            open.Clear();
            Match match = TagReg.Match(richText);
            while(match.Success)
            {
                if(match.Value == TagSuffix)
                {
                    if(open.Count > 0)
                    {
                        InterpretInfo iinfo = open.Pop();
                        iinfo.end = match.Index - 1;
                        if(iinfo.end >= iinfo.start)
                        {
                            Tag tag = iinfo.ToTag();
                            if(tag != null) close.Add(tag);
                        }
                    }
                } else
                {
                    InterpretInfo iinfo = new InterpretInfo();
                    iinfo.str = match.Value;
                    iinfo.start = match.Index + match.Length;
                    open.Push(iinfo);
                }
                match = match.NextMatch();
            }
            tags = close;
        }
    }

    private class InterpretInfo
    {
        private static readonly Regex TagReg = new Regex(@"<material=([^>\s]+)([^>]*)>");
        private static readonly Regex ItemReg = new Regex(@"(\w+)=([^\s]+)");
        public string str;
        public int start;
        public int end;
        public Tag ToTag()
        {
            Tag tag = null;
            Match match = TagReg.Match(str);
            if(match.Success)
            {
                string type = match.Groups[1].Value;
                if(!type.StartsWith("#"))
                {
                    var values = ItemReg.Matches(match.Groups[2].Value);
                    switch(type)
                    {
                        case "shadow":
                            tag = new Shadow();
                            break;

                        case "outline":
                            tag = new Outline();
                            break;

                        case "gradient":
                            tag = new GradientL();
                            break;

                        case "underline":
                            tag = new Underline();
                            break;
                    }
                    if(tag != null)
                    {
                        tag.start = start;
                        tag.end = end;
                        for(int i = 0; i < values.Count; i++)
                        {
                            string name = values[i].Groups[1].Value;
                            string value = values[i].Groups[2].Value;
                            tag.SetValue(name, value);
                        }
                    }
                }
            }
            return tag;
        }
    }

    private enum TagType
    {
        None,
        Shadow,
        Outline,
        Gradient,
        Underline,
    }

    private abstract class Tag
    {
        public int start;
        public int end;
        public virtual TagType type
        {
            get{
                return TagType.None;
            }
        }
        public virtual void SetValue(string name, string value)
        {
        }
    }

    private class Shadow : Tag
    {
        public Color c = Color.black;
        public float x = 1;
        public float y = -1;
        public override TagType type
        {
            get{
                return TagType.Shadow;
            }
        }
        public override void SetValue(string name, string value)
        {
            base.SetValue(name, value);
            switch(name)
            {
                case "c":
                    ColorUtility.TryParseHtmlString(value, out c);
                    break;

                case "x":
                    float.TryParse(value, out x);
                    break;

                case "y":
                    float.TryParse(value, out y);
                    break;
            }
        }
    }

    private class Outline : Shadow
    {
        public override TagType type
        {
            get{
                return TagType.Outline;
            }
        }
    }

    private class GradientL : Tag
    {
        public Color from = Color.white;
        public Color to = Color.black;
        public float x = 0;
        public float y = -1;
        public override TagType type
        {
            get{
                return TagType.Gradient;
            }
        }
        public override void SetValue(string name, string value)
        {
            base.SetValue(name, value);
            switch(name)
            {
                case "from":
                    ColorUtility.TryParseHtmlString(value, out from);
                    break;

                case "to":
                    ColorUtility.TryParseHtmlString(value, out to);
                    break;

                case "x":
                    float.TryParse(value, out x);
                    break;

                case "y":
                    float.TryParse(value, out y);
                    break;
            }
        }
    }

    private class Underline : Tag
    {
        public Color c = Color.white;
        public float h = 1.5f;
        public string e;
        public string args;
        public override TagType type
        {
            get{
                return TagType.Underline;
            }
        }
        public override void SetValue(string name, string value)
        {
            base.SetValue(name, value);
            switch(name)
            {
                case "c":
                    ColorUtility.TryParseHtmlString(value, out c);
                    break;

                case "h":
                    float.TryParse(value, out h);
                    break;

                case "event":
                    e = value;
                    break;

                case "args":
                    args = value;
                    break;
            }
        }
    }
}