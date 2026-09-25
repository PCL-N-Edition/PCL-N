# 输入硬件检测补充

输入事实描述操作系统公开的设备能力，不证明游戏已经启用此输入方式，也不主动发送振动。
Linux 从 sysfs input 设备的能力位图读取键盘、鼠标、触摸、笔、控制器和 force feedback；
不用 DISPLAY、设备名称或 js 节点存在与否代替能力。IIO angular velocity 通道描述陀螺仪。
枚举或位图读取不完整时，否定事实降级为 Unknown，已观察到的正向事实仍保留。
设备枚举与文本读取有固定上限；热插拔失败允许下一次刷新重试。

Windows XInput 没有陀螺仪查询，故该路径报告 Unknown，不再把未探测写成 false。
macOS 通过 IOHIDManager 枚举，再用 IOHIDDeviceConformsTo 检查 usage collections，
不用单个 PrimaryUsage 代替整个复合设备；CF 对象释放，不打开设备、不安装全局输入 hook。
macOS 运动传感器/振动、Windows 非 XInput 控制器传感器仍需各自的平台通道，不能用此提交宣称完成。

参考内核输入协议：https://docs.kernel.org/input/event-codes.html 和
https://docs.kernel.org/input/ff.html 。sysfs 暴露的是支持的效果，不是当前用户对 evdev 的写权限。
