package main

import (
	"encoding/json"
	"os"
	"os/exec"
	"strings"
	"testing"
)

func newsFixture() []NewsItem {
	return []NewsItem{
		{ID: "a1", Date: "23 Nov 2026", Title: "Newest", URL: "https://YOUR-DOMAIN.example/news", Summary: strings.Repeat("word ", 200)},
		{ID: "b2", Date: "22 Nov 2026", Title: "Second", URL: "https://evil.example/x"},
		{ID: "", Date: "22 Nov 2026", Title: "no id, dropped"},
		{ID: "c3", Date: "21 Nov 2026", Title: "Third", URL: "javascript:alert(1)"},
		{ID: "d4", Date: "20 Nov 2026", Title: "Fourth, over the cap"},
	}
}

func TestCleanNewsCapsAndSafeURL(t *testing.T) {
	got := CleanNews(newsFixture())
	if len(got) != 3 || got[0].ID != "a1" || got[1].ID != "b2" || got[2].ID != "c3" {
		t.Fatalf("items %+v", got)
	}
	if len(got[0].Summary) > newsMaxSummary+3 || !strings.HasSuffix(got[0].Summary, "...") {
		t.Fatalf("summary not shortened: %d %q", len(got[0].Summary), got[0].Summary[len(got[0].Summary)-10:])
	}
	for _, n := range got[1:] {
		if n.URL != "https://YOUR-DOMAIN.example/news" {
			t.Fatalf("unsafe url kept: %q", n.URL)
		}
	}
	for _, u := range []string{"http://YOUR-DOMAIN.example/news", "https://YOUR-DOMAIN.example.evil/x", "https://u@YOUR-DOMAIN.example/", "file:///etc/passwd"} {
		if safeNewsURL(u) {
			t.Fatalf("%s accepted", u)
		}
	}
}

func TestShortenUTF8(t *testing.T) {
	s := shorten(strings.Repeat("é", 400), 301)
	if !strings.HasSuffix(s, "...") || strings.ContainsRune(s, '�') || len(s) > 304 {
		t.Fatalf("bad cut %q", s)
	}
}

func TestPendingAndHide(t *testing.T) {
	items := newsFixture()
	if p := PendingNews(items, []string{"a1"}); len(p) != 2 || p[0].ID != "b2" {
		t.Fatalf("pending %+v", p)
	}
	var hidden []string
	for i := 0; i < 60; i++ {
		hidden = HideNews(hidden, []NewsItem{{ID: string(rune('A'+i%26)) + strings.Repeat("x", i)}})
	}
	if len(hidden) != newsMaxHidden {
		t.Fatalf("hidden not capped: %d", len(hidden))
	}
	if h := HideNews([]string{"a1"}, []NewsItem{{ID: "a1"}, {ID: "b2"}}); len(h) != 2 {
		t.Fatalf("duplicate kept: %v", h)
	}
	txt := NewsText(CleanNews(items))
	if !strings.HasPrefix(txt, "23 Nov 2026: Newest\n") || !strings.Contains(txt, "21 Nov 2026: Third") || strings.Count(txt, "word") > 70 {
		t.Fatalf("text %q", txt)
	}
}

func TestWhatsNewChoices(t *testing.T) {
	dir := t.TempDir()
	c := &Config{DataDir: dir, News: newsFixture()}
	var asked int
	var opened []string
	defer func(a func(string) int, o func(string)) { askNewsFn, openURLFn = a, o }(askNewsFn, openURLFn)
	openURLFn = func(u string) { opened = append(opened, u) }

	choice := NewsPlay
	askNewsFn = func(string) int { asked++; return choice }
	whatsNew(c, false) // Play: nothing stored, shown again next time
	choice = NewsRead
	whatsNew(c, false) // Read more: opens the newest, still not hidden
	if asked != 2 || len(opened) != 1 || opened[0] != "https://YOUR-DOMAIN.example/news" || len(readState(dir).NewsHidden) != 0 {
		t.Fatalf("asked %d opened %v hidden %v", asked, opened, readState(dir).NewsHidden)
	}
	choice = NewsHide
	st := readState(dir)
	st.ClientVersion = "1.5.7" // other state must survive
	writeState(dir, st)
	whatsNew(c, false)
	st = readState(dir)
	if len(st.NewsHidden) != 3 || st.ClientVersion != "1.5.7" {
		t.Fatalf("after hide %+v", st)
	}
	whatsNew(c, false) // everything hidden: no box
	if asked != 3 {
		t.Fatalf("asked again after hide: %d", asked)
	}
	// a new item shows up alone
	c.News = append([]NewsItem{{ID: "n9", Date: "24 Nov 2026", Title: "New"}}, c.News...)
	whatsNew(c, false)
	if asked != 4 {
		t.Fatalf("new item not shown")
	}
}

func TestFirstInstallHidesQuietly(t *testing.T) {
	dir := t.TempDir()
	defer func(a func(string) int) { askNewsFn = a }(askNewsFn)
	askNewsFn = func(string) int { t.Fatal("dialog on first install"); return NewsPlay }
	whatsNew(&Config{DataDir: dir, News: newsFixture()}, true)
	if len(readState(dir).NewsHidden) != 3 {
		t.Fatalf("first install did not mark items")
	}
}

func TestNewsReachesConfigAndOldLaunchersIgnoreIt(t *testing.T) {
	r := newRig(t, "windows", "1.5.7", clientFiles("windows"))
	r.manifest.News = newsFixture()[:1]
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	if len(r.cfg.News) != 1 || r.cfg.News[0].ID != "a1" {
		t.Fatalf("news %+v", r.cfg.News)
	}
	// launchers up to 1.0.3 decode into a Manifest without News: the key must be ignored, everything else intact
	type oldManifest struct {
		Serial   int64           `json:"serial"`
		Launcher map[string]File `json:"launcher"`
		Client   map[string]File `json:"client"`
		Native   map[string]File `json:"native,omitempty"`
	}
	b, _ := json.Marshal(r.manifest)
	var old oldManifest
	if err := json.Unmarshal(b, &old); err != nil || old.Client["windows"].Version != "1.5.7" {
		t.Fatalf("old decode %v %+v", err, old)
	}
}

func TestNewsFromSiteScript(t *testing.T) {
	if _, err := exec.LookPath("python3"); err != nil {
		t.Skip("no python3")
	}
	src := t.TempDir() + "/_news.html"
	os.WriteFile(src, []byte(`{# x #}
{% set items = [
  dict(date="23 Nov 2026", title="One", text="First   post text.", link=""),
  dict(date="22 Nov 2026", title="Two", text="`+strings.Repeat("long ", 100)+`", link="/download"),
  dict(date="21 Nov 2026", title="Three", text="c", link=""),
  dict(date="20 Nov 2026", title="Four", text="d", link=""),
] %}`), 0o644)
	out, err := exec.Command("python3", "-I", "tools/news_from_site.py", src).Output()
	if err != nil {
		t.Fatal(err)
	}
	var got []NewsItem
	if err := json.Unmarshal(out, &got); err != nil || len(got) != 3 {
		t.Fatalf("%v %s", err, out)
	}
	if got[0].Summary != "First post text." || len(got[0].ID) != 12 || got[0].URL != "https://YOUR-DOMAIN.example/news" || len(got[1].Summary) > 303 {
		t.Fatalf("%+v", got)
	}
	if c := CleanNews(got); len(c) != 3 {
		t.Fatalf("launcher rejects script output: %+v", c)
	}
	// a broken template gives [] (publish goes on without news)
	os.WriteFile(src, []byte("no list here"), 0o644)
	out, err = exec.Command("python3", "-I", "tools/news_from_site.py", src).Output()
	if err != nil || strings.TrimSpace(string(out)) != "[]" {
		t.Fatalf("broken template: %v %q", err, out)
	}
}
