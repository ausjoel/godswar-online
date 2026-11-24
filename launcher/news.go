package main

import (
	"fmt"
	"net/url"
	"strings"
	"unicode/utf8"
)

// NewsItem (launcher 1.0.4+): a player-site news post, copied into the signed manifest by publish_downloads.sh
// (tools/news_from_site.py reads signup/templates/_news.html). Older launchers ignore the "news" key.
type NewsItem struct {
	ID      string `json:"id"`
	Date    string `json:"date"`
	Title   string `json:"title"`
	URL     string `json:"url"`
	Summary string `json:"summary"`
}

const (
	newsMaxItems   = 3
	newsMaxTitle   = 200
	newsMaxSummary = 300
	newsMaxHidden  = 50
	// NewsHost is the only place "Read more" may open.
	NewsHost = "YOUR-DOMAIN.example"
)

// News choices from the dialog.
const (
	NewsPlay = iota // closed, timed out or "Play"
	NewsRead        // open the full post, then play
	NewsHide        // don't show these items again, then play
)

// shorten cuts s to at most n bytes at a word boundary and adds "...".
func shorten(s string, n int) string {
	s = strings.Join(strings.Fields(s), " ")
	if len(s) <= n {
		return s
	}
	cut := s[:n]
	for !utf8.ValidString(cut) {
		cut = cut[:len(cut)-1]
	}
	if i := strings.LastIndexByte(cut, ' '); i > n/2 {
		cut = cut[:i]
	}
	return strings.TrimRight(cut, " ,;:.") + "..."
}

// safeNewsURL: https on the player site only (the launcher hands it to `open` / the Windows shell).
func safeNewsURL(raw string) bool {
	u, err := url.Parse(raw)
	return err == nil && u.Scheme == "https" && u.Host == NewsHost && u.User == nil
}

// CleanNews keeps at most newsMaxItems well-formed items, with capped lengths and a safe link.
func CleanNews(in []NewsItem) []NewsItem {
	var out []NewsItem
	for _, n := range in {
		if len(out) == newsMaxItems {
			break
		}
		n.ID, n.Title, n.Date = strings.TrimSpace(n.ID), shorten(n.Title, newsMaxTitle), shorten(n.Date, 40)
		n.Summary = shorten(n.Summary, newsMaxSummary)
		if n.ID == "" || len(n.ID) > 64 || n.Title == "" {
			continue
		}
		if !safeNewsURL(n.URL) {
			n.URL = "https://" + NewsHost + "/news"
		}
		out = append(out, n)
	}
	return out
}

// PendingNews: the cleaned items the player has not hidden.
func PendingNews(items []NewsItem, hidden []string) []NewsItem {
	h := map[string]bool{}
	for _, id := range hidden {
		h[id] = true
	}
	var out []NewsItem
	for _, n := range CleanNews(items) {
		if !h[n.ID] {
			out = append(out, n)
		}
	}
	return out
}

// HideNews adds ids to the hidden list, newest last, capped at newsMaxHidden.
func HideNews(hidden []string, items []NewsItem) []string {
	seen := map[string]bool{}
	for _, id := range hidden {
		seen[id] = true
	}
	for _, n := range items {
		if !seen[n.ID] {
			hidden = append(hidden, n.ID)
			seen[n.ID] = true
		}
	}
	if len(hidden) > newsMaxHidden {
		hidden = hidden[len(hidden)-newsMaxHidden:]
	}
	return hidden
}

// NewsText is the compact box body: every item's date and title, the newest one's summary.
func NewsText(items []NewsItem) string {
	var b strings.Builder
	for i, n := range items {
		if i > 0 {
			b.WriteString("\n")
		}
		fmt.Fprintf(&b, "%s: %s", n.Date, n.Title)
		if i == 0 && n.Summary != "" {
			b.WriteString("\n" + n.Summary + "\n")
		}
	}
	return b.String()
}

// Dialog and browser hooks (replaced in tests so no dialog appears).
var askNewsFn, openURLFn = askNews, openURL

// whatsNew shows the news box once before launch and records "don't show again".
// firstInstall: a brand-new player has just seen the engine and download dialogs, so the current items are
// marked as hidden quietly instead of adding a third box.
func whatsNew(c *Config, firstInstall bool) {
	st := readState(c.DataDir)
	items := PendingNews(c.News, st.NewsHidden)
	if len(items) == 0 {
		return
	}
	choice := NewsHide
	if !firstInstall {
		choice = askNewsFn(NewsText(items))
	}
	switch choice {
	case NewsRead:
		openURLFn(items[0].URL)
	case NewsHide:
		st = readState(c.DataDir)
		st.NewsHidden = HideNews(st.NewsHidden, items)
		writeState(c.DataDir, st)
	}
}
