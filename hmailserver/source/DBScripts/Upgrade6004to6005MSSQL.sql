ALTER TABLE hm_messagerecipients ADD recipientdsnnotify int NOT NULL DEFAULT 0

update hm_dbversion set value = 6005
