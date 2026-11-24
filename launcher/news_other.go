//go:build !darwin && !windows

package main

import "fmt"

func askNews(text string) int { fmt.Println("What's new:\n" + text); return NewsPlay }

func openURL(u string) {}
