using UnityEngine;
using UnityEngine.UI;


public class TestRichText : MonoBehaviour
{
    public ZUIRichText richText;
    private void Start()
    {
        richText.SetImageSetter(setImage);
        richText.AddListener(onTextClick);

        richText.text = @"<material=shadow c=#000000 x=1 y=-1>阴影可控制</material><material=shadow c=#000000 x=2 y=-2>大小</material><material=shadow c=#000000 x=-2 y=2>方向</material>
<material=outline c=#000000 x=1 y=-1>描边可控制</material><material=outline c=#000000 x=2 y=-2>粗细</material>
<material=gradient from=#33FF33 to=#000000 x=0 y=-1>渐变色</material><material=gradient from=#FF3300 to=#000000 x=1 y=-1>可控制渐变方向</material>
<material=underline c=#FFFFFF h=1.5 event=uuuu args=uLink>下划线和图片可以添加点击事件（看日志）</material>
<material=underline c=#FFFFFF h=1.5 event=uuuu args=uLink><color=#123456>下划线颜色为RichText.color颜色</color></material>
图<material=image sprite=a atlas=c pivot=0 event=e1 args=a1 /></material>文<material=outline c=#000000 x=1 y=-1><color=#ff3300><size=20><i>嵌套变色变小倾斜描边</i></size></color></material>测试<material=image sprite=a atlas=c pivot=-1 event=e2 args=a2 /></material>混<material=image sprite=a atlas=c pivot=0 w=30 h=30 event=e3 args=a3 /></material>排<material=image frame=10 sprite=1+2 atlas=3 pivot=0 w=30 h=40 event=e4 args=a4 /></material>ABC
<material=gradient from=#FF3300 to=#000000 x=1 y=-1><b><size=50>嵌套渐变色变大加粗</size></b></material><material=image sprite=a atlas=c pivot=-1 event=e5 args=a5 /></material>";
    }

    private void setImage(ZUIImage image, string atlas, string sprite)
    {
        Debug.Log("设置纹理>" + atlas + "." + sprite);
        image.sprite = Resources.Load<Sprite>(sprite);
    }

    private void onTextClick(string evt, string arg)
    {
        Debug.LogWarning("点击触发>" + evt + "." + arg);
    }
}
