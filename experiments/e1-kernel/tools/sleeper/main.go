package main

import (
	"fmt"
	"os"
	"time"
)

func main() {
	ms := 500
	if len(os.Args) > 1 {
		if _, err := fmt.Sscan(os.Args[1], &ms); err != nil || ms <= 0 {
			ms = 500
		}
	}
	time.Sleep(time.Duration(ms) * time.Millisecond)
}
