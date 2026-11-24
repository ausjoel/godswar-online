// Objective-C half of gui_window_darwin.go (the launcher window on a Mac).
#import <Cocoa/Cocoa.h>
#include <stdlib.h>

extern int gwuiEvent(int kind, int x, int y);

static NSWindow *gwWin;
static int gwW, gwH;

@interface GWWindow : NSWindow
@end
@implementation GWWindow
- (BOOL)canBecomeKeyWindow { return YES; }
- (BOOL)canBecomeMainWindow { return YES; }
@end

@interface GWView : NSView
@property (nonatomic) CGImageRef img;
@end
@implementation GWView
- (BOOL)acceptsFirstMouse:(NSEvent *)e { return YES; }
- (BOOL)isOpaque { return NO; }
- (void)updateTrackingAreas {
	for (NSTrackingArea *t in self.trackingAreas) [self removeTrackingArea:t];
	[self addTrackingArea:[[NSTrackingArea alloc] initWithRect:self.bounds
		options:NSTrackingMouseMoved|NSTrackingMouseEnteredAndExited|NSTrackingActiveAlways owner:self userInfo:nil]];
	[super updateTrackingAreas];
}
- (NSPoint)pt:(NSEvent *)e { NSPoint p = [self convertPoint:e.locationInWindow fromView:nil]; return NSMakePoint(p.x, gwH - p.y); }
- (void)mouseMoved:(NSEvent *)e { NSPoint p = [self pt:e]; gwuiEvent(1, p.x, p.y); }
- (void)mouseDragged:(NSEvent *)e { NSPoint p = [self pt:e]; gwuiEvent(1, p.x, p.y); }
- (void)mouseExited:(NSEvent *)e { gwuiEvent(4, 0, 0); }
- (void)mouseDown:(NSEvent *)e {
	NSPoint p = [self pt:e];
	if (!gwuiEvent(2, p.x, p.y)) [self.window performWindowDragWithEvent:e];
}
- (void)mouseUp:(NSEvent *)e { NSPoint p = [self pt:e]; gwuiEvent(3, p.x, p.y); }
- (void)drawRect:(NSRect)r {
	CGContextRef c = NSGraphicsContext.currentContext.CGContext;
	CGContextClearRect(c, self.bounds);
	if (self.img) CGContextDrawImage(c, CGRectMake(0, 0, gwW, gwH), self.img);
}
- (void)dealloc { if (_img) CGImageRelease(_img); }
@end

@interface GWDelegate : NSObject <NSApplicationDelegate, NSWindowDelegate>
@end
@implementation GWDelegate
- (BOOL)windowShouldClose:(NSWindow *)w { gwuiEvent(5, 0, 0); return NO; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)a { gwuiEvent(5, 0, 0); return NSTerminateCancel; }
- (BOOL)applicationShouldHandleReopen:(NSApplication *)a hasVisibleWindows:(BOOL)v { [gwWin makeKeyAndOrderFront:nil]; return YES; }
@end

static GWDelegate *gwDel;

void gwuiRun(int w, int h, const char *title) {
	@autoreleasepool {
		gwW = w; gwH = h;
		[NSApplication sharedApplication];
		[NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
		gwDel = [GWDelegate new];
		NSApp.delegate = gwDel;
		NSMenu *bar = [NSMenu new], *appMenu = [NSMenu new];
		NSMenuItem *it = [NSMenuItem new];
		[bar addItem:it];
		[appMenu addItemWithTitle:@"Hide GodsWar" action:@selector(hide:) keyEquivalent:@"h"];
		[appMenu addItemWithTitle:@"Quit GodsWar" action:@selector(terminate:) keyEquivalent:@"q"];
		it.submenu = appMenu;
		NSApp.mainMenu = bar;
		gwWin = [[GWWindow alloc] initWithContentRect:NSMakeRect(0, 0, w, h)
			styleMask:NSWindowStyleMaskBorderless|NSWindowStyleMaskMiniaturizable backing:NSBackingStoreBuffered defer:NO];
		gwWin.title = [NSString stringWithUTF8String:title];
		gwWin.opaque = NO;
		gwWin.backgroundColor = NSColor.clearColor;
		gwWin.hasShadow = YES;
		gwWin.delegate = gwDel;
		gwWin.releasedWhenClosed = NO;
		GWView *v = [[GWView alloc] initWithFrame:NSMakeRect(0, 0, w, h)];
		gwWin.contentView = v;
		gwWin.acceptsMouseMovedEvents = YES;
		[gwWin center];
		[gwWin makeKeyAndOrderFront:nil];
		[NSApp activateIgnoringOtherApps:YES];
		[NSApp run];
		[gwWin orderOut:nil];
	}
}

// gwuiPresent copies a premultiplied RGBA frame and shows it on the main thread.
void gwuiPresent(const void *rgba, int w, int h) {
	CFDataRef d = CFDataCreate(NULL, rgba, (CFIndex)w * h * 4);
	CGDataProviderRef p = CGDataProviderCreateWithCFData(d);
	CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
	CGImageRef img = CGImageCreate(w, h, 8, 32, w * 4, cs, kCGImageAlphaPremultipliedLast | kCGBitmapByteOrderDefault,
		p, NULL, false, kCGRenderingIntentDefault);
	CGColorSpaceRelease(cs); CGDataProviderRelease(p); CFRelease(d);
	dispatch_async(dispatch_get_main_queue(), ^{
		GWView *v = (GWView *)gwWin.contentView;
		if (v.img) CGImageRelease(v.img);
		v.img = img;
		[v setNeedsDisplay:YES];
		[gwWin invalidateShadow];
	});
}

// gwuiScale: the highest backing scale of the attached screens (2 on a Retina Mac), so the window stays sharp
// wherever it is dragged; on a 1x screen macOS scales the 2x frame down.
int gwuiScale(void) {
	CGFloat s = 1;
	for (NSScreen *sc in NSScreen.screens) if (sc.backingScaleFactor > s) s = sc.backingScaleFactor;
	return s >= 2 ? 2 : 1;
}

void gwuiQuit(void) {
	dispatch_async(dispatch_get_main_queue(), ^{
		[NSApp stop:nil];
		NSEvent *e = [NSEvent otherEventWithType:NSEventTypeApplicationDefined location:NSZeroPoint modifierFlags:0
			timestamp:0 windowNumber:0 context:nil subtype:0 data1:0 data2:0];
		[NSApp postEvent:e atStart:YES];
	});
}