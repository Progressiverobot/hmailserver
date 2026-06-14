ALTER TABLE hm_messagerecipients ADD COLUMN recipientdsnnotify int NOT NULL DEFAULT 0

update hm_dbversion set value = 6005
