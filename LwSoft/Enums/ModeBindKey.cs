﻿namespace LwSoft.Enums
{
    /*
     mode 整形数: 模式。 取值有以几下种  该参数的缺省值为0

         0 : 推荐模式此模式比较通用，而且后台效果是最好的，并且会隐藏目标进程中的lw.dll.

         1 : 如果嫌用模式0绑定慢，或者模式0和模式2会造成目标进程崩溃可以用这个模式，这个模式绑定速度最快。

         2 : 同模式0,如果模式0或者模式1绑定失败，可以尝试这个模式。

         3：如果前几个模式都绑定不了，一定要试试这个（如果绑定不成功，可能要较长时间判断）。

         4：强力绑定模式，如果前面几个绑不上，一定要试试这个。
    */
    public class ModeBindKey
    {
    }
}