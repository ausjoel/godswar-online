//go:build windows

package main

import (
	"os/exec"
	"syscall"
	"unsafe"
)

var procMessageBoxW = syscall.NewLazyDLL("user32.dll").NewProc("MessageBoxW")

const (
	mbYesNoCancel   = 0x3
	mbIconInfo      = 0x40
	mbSetForeground = 0x10000
	mbTopmost       = 0x40000
	idYes, idNo     = 6, 7
)

// askNews: a standard Windows message box. Closing it (X or Esc) is Cancel, which means Play, never "hide".
func askNews(text string) int {
	body := text + "\n\nYes: read the full post, then play\nNo: don't show this again, then play\nCancel: play now"
	t, _ := syscall.UTF16PtrFromString(body)
	caption, _ := syscall.UTF16PtrFromString("What's new in GodsWar")
	r, _, _ := procMessageBoxW.Call(0, uintptr(unsafe.Pointer(t)), uintptr(unsafe.Pointer(caption)), mbYesNoCancel|mbIconInfo|mbSetForeground|mbTopmost)
	switch r {
	case idYes:
		return NewsRead
	case idNo:
		return NewsHide
	}
	return NewsPlay
}

func openURL(u string) {
	if safeNewsURL(u) {
		exec.Command("rundll32", "url.dll,FileProtocolHandler", u).Start()
	}
}

func init() {
	failBox = func(msg string) {
		t, _ := syscall.UTF16PtrFromString(msg)
		caption, _ := syscall.UTF16PtrFromString("GodsWar")
		procMessageBoxW.Call(0, uintptr(unsafe.Pointer(t)), uintptr(unsafe.Pointer(caption)), 0x10|mbSetForeground)
	}
}
