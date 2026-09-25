#include <jni.h>
#include <pthread.h>
#include <unistd.h>
#import <Cocoa/Cocoa.h>

JNIEXPORT jint JNICALL Java_JvmHostSmoke_checkCocoaThread(JNIEnv *env, jclass type) {
    (void)env;
    (void)type;
    if (!pthread_main_np() || ![NSThread isMainThread]) return -1;
    @autoreleasepool {
        [NSApplication sharedApplication];
        NSWindow *window = [[NSWindow alloc]
            initWithContentRect:NSMakeRect(0, 0, 64, 64)
            styleMask:NSWindowStyleMaskTitled
            backing:NSBackingStoreBuffered defer:NO];
        if (window == nil) return -2;
        [window setReleasedWhenClosed:NO];
        [window close];
        [window release];
    }
    return (jint)getpid();
}
