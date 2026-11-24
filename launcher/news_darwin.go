package main

import (
	"context"
	"fmt"
	"os/exec"
	"strings"
	"time"
)

// askNews: one dialog before the game opens; no answer within a minute counts as Play. The context cap stops a
// dialog that cannot appear (locked screen: osascript otherwise waits for its 2-minute AppleEvent timeout).
func askNews(text string) int {
	ctx, cancel := context.WithTimeout(context.Background(), 75*time.Second)
	defer cancel()
	out, _ := exec.CommandContext(ctx, "/usr/bin/osascript", "-e", fmt.Sprintf(`button returned of (display dialog "%s" with title "What's new in GodsWar" buttons {"Don't show again", "Read more", "Play"} default button "Play" giving up after 60)`, esc(text))).Output()
	switch strings.TrimSpace(string(out)) {
	case "Read more":
		return NewsRead
	case "Don't show again":
		return NewsHide
	}
	return NewsPlay
}

func openURL(u string) {
	if safeNewsURL(u) {
		exec.Command("/usr/bin/open", u).Run()
	}
}
